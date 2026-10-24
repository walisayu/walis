using Walis.Core.Jenkins;
using Walis.Core.Models;

namespace Walis.Tests;

public class JenkinsImportTests
{
    private const string Declarative = """"
                                       pipeline {
                                           agent { docker { image 'node:20'; args '-e CI=true' } }
                                           environment {
                                               APP = 'walis'
                                               DEPLOY_TOKEN = credentials('deploy-token')
                                           }
                                           options {
                                               timeout(time: 2, unit: 'HOURS')
                                               disableConcurrentBuilds()
                                               buildDiscarder(logRotator(numToKeepStr: '10'))
                                           }
                                           parameters {
                                               string(name: 'ENV', defaultValue: 'stg', description: '目標環境')
                                               choice(name: 'REGION', choices: ['tw', 'jp'])
                                               booleanParam(name: 'DRY_RUN', defaultValue: true)
                                               password(name: 'SECRET')
                                           }
                                           triggers { cron('H 2 * * 1-5') }
                                           tools { nodejs 'node20' }
                                           stages {
                                               stage('Build') {
                                                   options { retry(2) }
                                                   steps {
                                                       sh 'npm ci'
                                                       sh """
                                                           npm run build -- --env ${params.ENV}
                                                           echo "build $BUILD_NUMBER on ${env.APP}"
                                                       """
                                                   }
                                               }
                                               stage('Test') {
                                                   parallel {
                                                       stage('Unit') { steps { sh 'npm run test:unit' } }
                                                       stage('Lint') { steps { sh 'npm run lint' } }
                                                   }
                                               }
                                               stage('Deploy') {
                                                   when { branch 'main' }
                                                   environment { TARGET = 'prod' }
                                                   steps {
                                                       dir('deploy') {
                                                           withCredentials([usernamePassword(credentialsId: 'registry', usernameVariable: 'REG_USER', passwordVariable: 'REG_PASS')]) {
                                                               sh './deploy.sh'
                                                           }
                                                       }
                                                       build job: 'smoke-tests', wait: false
                                                       input message: 'Promote?'
                                                   }
                                                   post {
                                                       failure { sh './rollback.sh' }
                                                   }
                                               }
                                           }
                                           post {
                                               always { archiveArtifacts artifacts: 'dist/**, reports/*.xml' }
                                               success { junit 'reports/junit.xml' }
                                               failure { slackSend channel: '#ci', message: 'failed' }
                                           }
                                       }
                                       """";

    [Fact]
    public void DeclarativePipelineMapsToJobDraft()
    {
        var result = JenkinsImporter.Import(Declarative, "前端");
        var job = result.Job;

        Assert.Equal("declarative", result.Format);
        Assert.Equal("前端", job.Name);
        Assert.Equal(120, job.TimeoutMinutes);
        Assert.False(job.AllowConcurrentBuilds);
        Assert.Equal(TriggerMode.Cron, job.TriggerKind);
        Assert.Matches(@"^\d+ 2 \* \* 1-5$", job.Cron);

        // Parameters: four types, the password has no default.
        var parameters = BuildParameters.Parse(job.ParametersJson);
        Assert.Equal(["ENV", "REGION", "DRY_RUN", "SECRET"], parameters.Select(parameter => parameter.Name));
        Assert.Equal("stg", parameters[0].Default);
        Assert.Equal(["tw", "jp"], parameters[1].Options);
        Assert.Equal("true", parameters[2].Default);
        Assert.Equal(BuildParameterTypes.Password, parameters[3].Type);

        // Environment variables: plain ones carry values, credential ones are secrets waiting to be filled in.
        Assert.Contains(result.Environment, variable => variable is { Name: "APP", Value: "walis", IsSecret: false });
        Assert.Contains(result.Environment, variable => variable is { Name: "DEPLOY_TOKEN", IsSecret: true });
        Assert.Contains(result.Environment, variable => variable is { Name: "REG_USER", IsSecret: true });
        Assert.Contains(result.Environment, variable => variable is { Name: "REG_PASS", IsSecret: true });

        // Steps: docker agent -> run in container; parallel -> parallel; when -> guard; post -> run condition.
        var steps = job.Steps;
        // The pipeline-level post has only artifacts and notifications, nothing executable, so it adds no step.
        Assert.Equal(["Build", "Test / Unit", "Test / Lint", "Deploy", "Deploy / failure"],
            steps.Select(step => step.Name));
        Assert.All(steps.Take(4), step => Assert.Equal("container.run", step.ActionId));
        Assert.Equal("node:20", steps[0].Parameters["image"]);
        Assert.Equal("-e CI=true", steps[0].Parameters["args"]);
        Assert.Equal(2, steps[0].Retries);
        Assert.Contains("npm run build -- --env ${ENV}", steps[0].Parameters["script"]);
        Assert.Contains("echo \"build ${WALIS_BUILD_NUMBER} on ${APP}\"", steps[0].Parameters["script"]);

        Assert.False(steps[1].Parallel);
        Assert.True(steps[2].Parallel);

        var deploy = steps[3].Parameters["script"]!;
        Assert.Contains("export TARGET='prod'", deploy);
        Assert.Contains("[ \"$WALIS_BRANCH\" = 'main' ]", deploy);
        Assert.Contains("cd 'deploy'", deploy);
        Assert.Contains("./deploy.sh", deploy);
        Assert.Contains("# TODO input", deploy);

        Assert.Equal(StepRunCondition.Failure, steps[4].RunWhen);
        Assert.Contains("./rollback.sh", steps[4].Parameters["script"]);

        Assert.Equal("dist/**\nreports/*.xml\nreports/junit.xml", job.ArtifactPath);

        var downstream = DownstreamJobs.Parse(job.DownstreamJson);
        Assert.Single(downstream);
        Assert.Equal("smoke-tests", downstream[0].JobName);

        // Warnings: everything that cannot be mapped must be named.
        Assert.Contains(result.Warnings,
            warning => warning.Severity == JenkinsWarningSeverity.Unsupported && warning.Message.Contains("input"));
        Assert.Contains(result.Warnings, warning => warning.Message.Contains("slackSend"));
        Assert.Contains(result.Warnings, warning => warning.Message.Contains("nodejs"));
        Assert.Contains(result.Warnings, warning => warning.Message.Contains("build job"));
        Assert.Contains(result.Warnings, warning => warning.Message.Contains("buildDiscarder"));
        Assert.Contains(result.Warnings, warning => warning.Message.Contains("H 已換成固定值"));
    }

    [Fact]
    public void ParentStageWhenAndEnvironmentPropagateToParallelAndNestedChildStages()
    {
        var result = JenkinsImporter.Import("""
                                            pipeline {
                                                agent any
                                                stages {
                                                    stage('Test') {
                                                        when { environment name: 'SKIP', value: 'false' }
                                                        environment { SUITE = 'all' }
                                                        parallel {
                                                            stage('A') { steps { sh 'run a' } }
                                                            stage('B') { stages { stage('B1') { steps { sh 'run b1' } } } }
                                                        }
                                                    }
                                                }
                                            }
                                            """, null);

        Assert.Equal(["Test / A", "Test / B / B1"], result.Job.Steps.Select(step => step.Name));
        Assert.All(result.Job.Steps, step =>
        {
            var script = step.Parameters["command"]!;
            Assert.Contains("export SUITE='all'", script);
            Assert.Contains("[ \"$SKIP\" = 'false' ]", script);
        });
        Assert.True(result.Job.Steps[1].Parallel);
    }

    [Fact]
    public void ScriptedPipelineAndGroovyLogicAreLeftAsComments()
    {
        var result = JenkinsImporter.Import("""
                                            properties([
                                                pipelineTriggers([cron('@daily')]),
                                                parameters([string(name: 'TAG', defaultValue: 'latest')]),
                                                disableConcurrentBuilds()
                                            ])
                                            node('builder') {
                                                checkout scm
                                                stage('Build') {
                                                    sh "docker build -t app:${params.TAG} ."
                                                }
                                                stage('Verify') {
                                                    def out = sh(script: 'ls', returnStdout: true).trim()
                                                    if (out) { echo 'ok' }
                                                    retry(3) { sh './flaky.sh' }
                                                }
                                                parallel unit: { sh 'make unit' }, lint: { sh 'make lint' }
                                            }
                                            """, "映像");

        var job = result.Job;
        Assert.Equal("scripted", result.Format);
        Assert.Equal("builder", job.RunnerName);
        Assert.Equal(TriggerMode.Cron, job.TriggerKind);
        Assert.False(job.AllowConcurrentBuilds);
        Assert.Single(BuildParameters.Parse(job.ParametersJson));

        Assert.Equal(["前置", "Build", "Verify", "unit", "lint"], job.Steps.Select(step => step.Name));
        Assert.Contains("# TODO checkout", job.Steps[0].Parameters["command"]);
        Assert.Contains("docker build -t app:${TAG} .", job.Steps[1].Parameters["command"]);
        var verify = job.Steps[2].Parameters["command"]!;
        Assert.Contains("# TODO Groovy 變數：out", verify);
        Assert.Contains("# TODO Groovy：if (out)", verify);
        Assert.Contains("./flaky.sh", verify);
        Assert.Equal(0, job.Steps[2].Retries);
        Assert.True(job.Steps[4].Parallel);
        Assert.Contains(result.Warnings, warning => warning.Message.Contains("retry"));
    }

    [Fact]
    public void FreestyleConfigXmlMapsToJob()
    {
        var result = JenkinsImporter.Import("""
                                            <?xml version='1.1' encoding='UTF-8'?>
                                            <project>
                                              <description>每天打包</description>
                                              <properties>
                                                <hudson.model.ParametersDefinitionProperty>
                                                  <parameterDefinitions>
                                                    <hudson.model.StringParameterDefinition><name>VERSION</name><defaultValue>1.0</defaultValue></hudson.model.StringParameterDefinition>
                                                    <hudson.model.ChoiceParameterDefinition><name>ENV</name><choices class="java.util.Arrays$ArrayList"><a class="string-array"><string>stg</string><string>prod</string></a></choices></hudson.model.ChoiceParameterDefinition>
                                                  </parameterDefinitions>
                                                </hudson.model.ParametersDefinitionProperty>
                                              </properties>
                                              <scm class="hudson.plugins.git.GitSCM">
                                                <userRemoteConfigs><hudson.plugins.git.UserRemoteConfig><url>https://git.example.com/app.git</url></hudson.plugins.git.UserRemoteConfig></userRemoteConfigs>
                                                <branches><hudson.plugins.git.BranchSpec><name>*/develop</name></hudson.plugins.git.BranchSpec></branches>
                                              </scm>
                                              <assignedNode>linux</assignedNode>
                                              <disabled>true</disabled>
                                              <concurrentBuild>false</concurrentBuild>
                                              <triggers><hudson.triggers.TimerTrigger><spec>H 3 * * *</spec></hudson.triggers.TimerTrigger></triggers>
                                              <builders>
                                                <hudson.tasks.Shell><command>npm ci
                                            npm run build</command></hudson.tasks.Shell>
                                                <hudson.tasks.BatchFile><command>dir</command></hudson.tasks.BatchFile>
                                              </builders>
                                              <publishers>
                                                <hudson.tasks.ArtifactArchiver><artifacts>dist/**</artifacts></hudson.tasks.ArtifactArchiver>
                                                <hudson.tasks.BuildTrigger><childProjects>deploy-stg, notify</childProjects><threshold><name>SUCCESS</name></threshold></hudson.tasks.BuildTrigger>
                                              </publishers>
                                              <buildWrappers>
                                                <hudson.plugins.build__timeout.BuildTimeoutWrapper><strategy><timeoutMinutes>45</timeoutMinutes></strategy></hudson.plugins.build__timeout.BuildTimeoutWrapper>
                                              </buildWrappers>
                                            </project>
                                            """, "打包");

        var job = result.Job;
        Assert.Equal("freestyle", result.Format);
        Assert.Equal("每天打包", job.Description);
        Assert.Equal("https://git.example.com/app.git", job.Repository);
        Assert.Equal("develop", job.Branch);
        Assert.Equal("linux", job.RunnerName);
        Assert.True(job.IsDisabled);
        Assert.False(job.AllowConcurrentBuilds);
        Assert.Equal(45, job.TimeoutMinutes);
        Assert.Equal(TriggerMode.Cron, job.TriggerKind);
        Assert.Matches(@"^\d+ 3 \* \* \*$", job.Cron);
        Assert.Equal("dist/**", job.ArtifactPath);

        var parameters = BuildParameters.Parse(job.ParametersJson);
        Assert.Equal(["VERSION", "ENV"], parameters.Select(parameter => parameter.Name));
        Assert.Equal(["stg", "prod"], parameters[1].Options);

        Assert.Equal(["Checkout", "Shell 1", "Batch 2"], job.Steps.Select(step => step.Name));
        Assert.Contains("git clone --branch 'develop'", job.Steps[0].Parameters["command"]);
        Assert.Equal("npm ci\nnpm run build", job.Steps[1].Parameters["command"]);
        Assert.StartsWith("# TODO Windows", job.Steps[2].Parameters["command"]);

        Assert.Equal(["deploy-stg", "notify"],
            DownstreamJobs.Parse(job.DownstreamJson).Select(target => target.JobName));
        Assert.Contains(result.Warnings, warning => warning.Message.Contains("Windows"));
    }

    [Fact]
    public void PipelineJobConfigXmlConvertsEmbeddedScript()
    {
        var result = JenkinsImporter.Import("""
                                            <flow-definition plugin="workflow-job">
                                              <description>內嵌</description>
                                              <definition class="org.jenkinsci.plugins.workflow.cps.CpsFlowDefinition">
                                                <script>pipeline { agent any; stages { stage('Hi') { steps { echo 'hello' } } } }</script>
                                              </definition>
                                            </flow-definition>
                                            """, null);

        Assert.Equal("declarative", result.Format);
        Assert.Equal("內嵌", result.Job.Description);
        Assert.Single(result.Job.Steps);
        Assert.Equal("echo 'hello'", result.Job.Steps[0].Parameters["command"]);

        var missing = JenkinsImporter.Import("""
                                             <flow-definition><definition class="CpsScmFlowDefinition"><scriptPath>ci/Jenkinsfile</scriptPath></definition></flow-definition>
                                             """, null);
        Assert.Empty(missing.Job.Steps);
        Assert.Contains(missing.Warnings, warning => warning.Message.Contains("ci/Jenkinsfile"));
    }

    public static TheoryData<string> Samples()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Walis.slnx")))
            directory = Path.GetDirectoryName(directory);

        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(directory!, "docs", "jenkins-samples"))
                     .Where(file => !file.EndsWith(".md")).OrderBy(file => file)) data.Add(file);

        return data;
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void EveryFileInSamplesDirectoryConvertsToSteps(string path)
    {
        var result = JenkinsImporter.Import(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));

        Assert.NotEmpty(result.Job.Steps);
        Assert.All(result.Job.Steps,
            step => Assert.False(string.IsNullOrWhiteSpace(step.Parameters.GetValueOrDefault("command") ??
                                                           step.Parameters.GetValueOrDefault("script"))));
        Assert.DoesNotContain(result.Warnings, warning => warning.Message.Contains("不是 Jenkinsfile"));

        // Each file deliberately contains something unmappable, so the warnings must not be empty.
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void JenkinsCronSyntaxConvertsToFixedFiveFields()
    {
        var warnings = new List<JenkinsWarning>();
        Assert.Matches(@"^\d+ \d+ \* \* \*$", JenkinsCron.Convert("@daily", "x", warnings));
        Assert.Equal("*/15 * * * *", JenkinsCron.Convert("H/15 * * * *", "x", warnings));
        Assert.Matches(@"^([0-9]|1[0-9]|2[0-9]) 4 \* \* \*$", JenkinsCron.Convert("H(0-29) 4 * * *", "x", warnings));
        Assert.Equal("30 2 * * 1", JenkinsCron.Convert("30 2 * * 1", "x", warnings));
        Assert.Equal(JenkinsCron.Convert("H H * * *", "same", []), JenkinsCron.Convert("H H * * *", "same", []));
        Assert.Null(JenkinsCron.Convert("not a cron", "x", warnings));
        Assert.Matches(@"^\d+ 1 \* \* \*$",
            JenkinsCron.Convert("TZ=Asia/Taipei\nH 1 * * *\nH 13 * * *", "x", warnings));
        Assert.Contains(warnings, warning => warning.Message.Contains("時區"));
        Assert.Contains(warnings, warning => warning.Message.Contains("只取第一條"));
    }
}