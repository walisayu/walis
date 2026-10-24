<script setup lang="ts">
import { computed } from 'vue'

/**
 * AI replies are Markdown, but the content is untrusted, so v-html is not used:
 * only headings, lists, code blocks and paragraphs are parsed here, everything else is rendered as plain text.
 */
const props = defineProps<{ text: string }>()

type Block =
  | { kind: 'heading'; text: string }
  | { kind: 'list'; items: string[] }
  | { kind: 'code'; text: string }
  | { kind: 'paragraph'; text: string }

const blocks = computed<Block[]>(() => {
  const result: Block[] = []
  const lines = props.text.split('\n')
  let index = 0

  while (index < lines.length) {
    const line = lines[index]

    if (line.trimStart().startsWith('```')) {
      const code: string[] = []
      index += 1

      while (index < lines.length && !lines[index].trimStart().startsWith('```')) {
        code.push(lines[index])
        index += 1
      }

      index += 1
      result.push({ kind: 'code', text: code.join('\n') })
      continue
    }

    if (/^#{1,6}\s/.test(line)) {
      result.push({ kind: 'heading', text: line.replace(/^#{1,6}\s*/, '') })
      index += 1
      continue
    }

    if (/^\s*([-*]|\d+\.)\s/.test(line)) {
      const items: string[] = []

      while (index < lines.length && /^\s*([-*]|\d+\.)\s/.test(lines[index])) {
        items.push(lines[index].replace(/^\s*([-*]|\d+\.)\s*/, ''))
        index += 1
      }

      result.push({ kind: 'list', items })
      continue
    }

    if (line.trim().length === 0) {
      index += 1
      continue
    }

    const paragraph: string[] = []

    while (
      index < lines.length &&
      lines[index].trim().length > 0 &&
      !/^#{1,6}\s/.test(lines[index]) &&
      !/^\s*([-*]|\d+\.)\s/.test(lines[index]) &&
      !lines[index].trimStart().startsWith('```')
    ) {
      paragraph.push(lines[index])
      index += 1
    }

    result.push({ kind: 'paragraph', text: paragraph.join('\n') })
  }

  return result
})

/** Strips bold and inline-code markers and shows the content as is. */
function plain(value: string): string {
  return value.replace(/\*\*(.+?)\*\*/g, '$1').replace(/`(.+?)`/g, '$1')
}
</script>

<template>
  <div class="ai-answer">
    <template v-for="(block, index) in blocks" :key="index">
      <h6 v-if="block.kind === 'heading'" class="fw-semibold mt-3 mb-2">{{ plain(block.text) }}</h6>
      <ul v-else-if="block.kind === 'list'" class="mb-3">
        <li v-for="(item, itemIndex) in block.items" :key="itemIndex">{{ plain(item) }}</li>
      </ul>
      <pre
        v-else-if="block.kind === 'code'"
        class="bg-body-tertiary border rounded p-3 mb-3"
      ><code>{{ block.text }}</code></pre>
      <p v-else class="mb-3" style="white-space: pre-wrap">{{ plain(block.text) }}</p>
    </template>
  </div>
</template>
