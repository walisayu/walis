#!/usr/bin/env python3 -I
"""
generate-sbom.py - Produce a CycloneDX 1.5 JSON SBOM for Walis releases from repository artifacts.
Standard library only; does not require external tools or network access.

Usage:
  python3 -I deploy/generate-sbom.py <repo-root> <commit-sha> <out.cdx.json> [version] [release-staging]
"""
import base64
import datetime
import json
import os
import re
import sys
import uuid

NUGET_TARGETS = {"Api": "api", "Core": "core", "Runner": "runner", "Mcp": "mcp", "Tests": "tests"}
SCRIPT_NAME = "deploy/generate-sbom.py"
SCRIPT_VERSION = "1.0.0"


def read_json(path):
    with open(path, "r", encoding="utf-8") as fh:
        return json.load(fh)


def purl_nuget(name, version):
    return f"pkg:nuget/{name}@{version}"


def purl_npm(name, version):
    if name.startswith("@"):
        scope, rest = name[1:].split("/", 1)
        return f"pkg:npm/%40{scope}/{rest}@{version}"
    return f"pkg:npm/{name}@{version}"


def csproj_direct_refs(path):
    if not os.path.exists(path):
        return set()
    text = open(path, "r", encoding="utf-8").read()
    return {m.group(1).lower() for m in re.finditer(r'<PackageReference\s+Include="([^"]+)"', text)}


class Sbom:
    def __init__(self):
        self.components = {}
        self.deps = {}

    def add(self, ref, comp, target, direct=False, extra_props=None):
        c = self.components.get(ref)
        if c is None:
            c = dict(comp)
            c["bom-ref"] = ref
            c["properties"] = []
            self.components[ref] = c
        props = c["properties"]
        if {"name": "walis:target", "value": target} not in props:
            props.append({"name": "walis:target", "value": target})
        if direct and {"name": "walis:direct", "value": "true"} not in props:
            props.append({"name": "walis:direct", "value": "true"})
        for p in extra_props or []:
            if p not in props:
                props.append(p)
        return c

    def edge(self, src, dst):
        self.deps.setdefault(src, set()).add(dst)
        self.deps.setdefault(dst, set())


def add_nuget(sbom, root):
    counts = {}
    for proj, target in NUGET_TARGETS.items():
        assets_path = os.path.join(root, "source", f"Walis.{proj}", "obj", "project.assets.json")
        if not os.path.exists(assets_path):
            continue
        assets = read_json(assets_path)
        direct = csproj_direct_refs(os.path.join(root, "source", f"Walis.{proj}", f"Walis.{proj}.csproj"))
        libs = assets.get("libraries", {})
        tgt = assets.get("targets", {}).get("net10.0", {})
        n = 0
        for key, lib in libs.items():
            name, version = key.split("/", 1)
            if lib.get("type") == "project":
                continue
            ref = purl_nuget(name, version)
            scope = "excluded" if target == "tests" else "required"
            comp = {
                "type": "library",
                "name": name,
                "version": version,
                "purl": ref,
                "scope": scope,
            }
            sha = lib.get("sha512")
            if sha:
                digest = base64.b64decode(sha, validate=True)
                if len(digest) != 64:
                    raise ValueError(f"Invalid SHA-512 digest for {name}")
                comp["hashes"] = [{"alg": "SHA-512", "content": digest.hex()}]
            c = sbom.add(ref, comp, target, direct=name.lower() in direct)
            if scope == "required":
                c["scope"] = "required"
            n += 1
        counts[target] = n
        for key, entry in tgt.items():
            name, version = key.split("/", 1)
            if libs.get(key, {}).get("type") == "project":
                continue
            src = purl_nuget(name, version)
            for dname, _ in entry.get("dependencies", {}).items():
                found = next((k for k in libs if k.split("/", 1)[0].lower() == dname.lower()), None)
                if found and libs[found].get("type") != "project":
                    sbom.edge(src, purl_nuget(*found.split("/", 1)))
    return counts


def add_npm(sbom, root, rel, target_prod, target_dev):
    full_path = os.path.join(root, rel)
    if not os.path.exists(full_path):
        return {target_prod: 0, target_dev: 0}
    lock = read_json(full_path)
    pkgs = lock.get("packages", {})
    rootpkg = pkgs.get("", {})
    direct = set(rootpkg.get("dependencies", {})) | set(rootpkg.get("devDependencies", {}))
    counts = {target_prod: 0, target_dev: 0}
    path_to_ref = {}
    for path, info in pkgs.items():
        if not path:
            continue
        name = path.split("node_modules/")[-1]
        version = info.get("version")
        ref = purl_npm(name, version)
        is_dev = bool(info.get("dev")) or bool(info.get("devOptional"))
        target = target_dev if is_dev else target_prod
        if is_dev:
            scope = "excluded"
        elif info.get("optional"):
            scope = "optional"
        else:
            scope = "required"
        comp = {"type": "library", "name": name, "version": version, "purl": ref, "scope": scope}
        integ = info.get("integrity")
        if integ and integ.startswith("sha512-"):
            import base64
            comp["hashes"] = [{"alg": "SHA-512", "content": base64.b64decode(integ[7:]).hex()}]
        extra = []
        if info.get("devOptional"):
            extra.append({"name": "walis:npm-flag", "value": "devOptional"})
        if info.get("optional") and not is_dev:
            extra.append({"name": "walis:npm-flag", "value": "optional"})
        c = sbom.add(ref, comp, target, direct=name in direct, extra_props=extra)
        if scope == "required":
            c["scope"] = "required"
        counts[target] += 1
        path_to_ref[path] = ref

    for path, info in pkgs.items():
        if not path:
            continue
        src = path_to_ref[path]
        for dname in list(info.get("dependencies", {})) + list(info.get("optionalDependencies", {})):
            probe = path
            while True:
                cand = f"{probe}/node_modules/{dname}" if probe else f"node_modules/{dname}"
                if cand in path_to_ref:
                    sbom.edge(src, path_to_ref[cand])
                    break
                if not probe:
                    break
                idx = probe.rfind("/node_modules/")
                probe = probe[:idx] if idx >= 0 else ""
    return counts


def add_runtime(sbom, release_root=None):
    # Only inspect this release's staging tree; old dist/bin outputs are not evidence of what is being shipped.
    added = set()
    if release_root is None:
        return []
    if not os.path.isdir(release_root):
        raise ValueError("Release staging directory does not exist")
    targets = {"Walis.Api.runtimeconfig.json": "api", "Walis.Mcp.runtimeconfig.json": "mcp",
               "walis-runner.runtimeconfig.json": "runner"}
    for directory, _, files in os.walk(release_root):
        for filename in files:
            if filename not in targets:
                continue
            cfg = read_json(os.path.join(directory, filename))["runtimeOptions"]
            frameworks = cfg.get("includedFrameworks")
            if not frameworks:
                raise ValueError(f"Release is not self-contained: {filename}")
            target = targets[filename]
            for fw in frameworks:
                name = f"{fw['name']}.Runtime"
                ref = purl_nuget(name, fw["version"])
                comp = {"type": "framework", "name": name, "version": fw["version"], "purl": ref, "scope": "required"}
                sbom.add(ref, comp, target, extra_props=[{"name": "walis:source", "value": filename}])
                added.add((name, fw["version"], target))
    if not added:
        raise ValueError("Release staging contains no Walis runtime manifests")
    return sorted(added)


def main():
    if len(sys.argv) < 4:
        sys.stderr.write("Usage: python3 -I generate-sbom.py <repo-root> <commit-sha> <out.cdx.json> [version] [release-staging]\n")
        sys.exit(1)

    root = os.path.abspath(sys.argv[1])
    commit = sys.argv[2]
    out = sys.argv[3]
    version = sys.argv[4] if len(sys.argv) > 4 else "1.0.0"

    sbom = Sbom()
    nuget_counts = add_nuget(sbom, root)
    admin_counts = add_npm(sbom, root, "source/Walis.Admin/package-lock.json", "admin-ui", "admin-ui-dev")
    sdk_counts = add_npm(sbom, root, "source/walis-plugin-sdk/package-lock.json", "plugin-sdk", "plugin-sdk-dev")
    runtime = add_runtime(sbom, sys.argv[5] if len(sys.argv) > 5 else None)

    main_ref = f"pkg:generic/walis@{version}?vcs_url=git%2Bhttps%3A%2F%2Fgithub.com%2Fwalisayu%2Fwalis.git%40{commit}"
    doc = {
        "bomFormat": "CycloneDX",
        "specVersion": "1.5",
        "serialNumber": f"urn:uuid:{uuid.uuid4()}",
        "version": 1,
        "metadata": {
            "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "tools": [{"vendor": "Walis", "name": SCRIPT_NAME, "version": SCRIPT_VERSION}],
            "component": {
                "type": "application",
                "bom-ref": main_ref,
                "name": "Walis",
                "version": version,
                "purl": main_ref,
                "properties": [
                    {"name": "walis:commit", "value": commit},
                    {"name": "walis:target-framework", "value": "net10.0"},
                    {"name": "walis:method", "value": "lockfile-derived (project.assets.json, package-lock.json)"},
                    {"name": "walis:runtime-evidence", "value": "release-staging" if runtime else "not-supplied"},
                ],
            },
            "properties": [
                {"name": "walis:count:" + k, "value": str(v)}
                for k, v in list(nuget_counts.items()) + list(admin_counts.items()) + list(sdk_counts.items())
            ],
        },
        "components": sorted(sbom.components.values(), key=lambda c: c["purl"].lower()),
        "dependencies": [
            {"ref": r, "dependsOn": sorted(d)} for r, d in sorted(sbom.deps.items())
        ],
    }

    direct_refs = sorted(c["bom-ref"] for c in sbom.components.values()
                         if {"name": "walis:direct", "value": "true"} in c["properties"])
    doc["dependencies"].insert(0, {"ref": main_ref, "dependsOn": direct_refs})

    out_dir = os.path.dirname(out)
    if out_dir:
        os.makedirs(out_dir, exist_ok=True)

    with open(out, "w", encoding="utf-8") as fh:
        json.dump(doc, fh, indent=1, ensure_ascii=False)

    print(f"SBOM generated: {out} ({len(doc['components'])} components)")


if __name__ == "__main__":
    main()
