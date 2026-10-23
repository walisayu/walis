import { useI18n } from 'vue-i18n'
import { useSettingsStore } from '../stores/settings'

const localeTags: Record<string, string> = { 'zh-Hant': 'zh-Hant-TW', en: 'en-US', ja: 'ja-JP' }

export function useFormat() {
  const { locale, t } = useI18n()
  const settings = useSettingsStore()

  const tag = () => localeTags[locale.value as string] ?? 'zh-Hant-TW'
  const zone = () => settings.settings?.timeZone || undefined

  function dateTime(value: string | null | undefined): string {
    if (!value) {
      return '—'
    }

    return new Intl.DateTimeFormat(tag(), {
      dateStyle: 'medium',
      timeStyle: 'short',
      hourCycle: 'h23',
      timeZone: zone()
    }).format(new Date(value))
  }

  function time(value: string | null | undefined): string {
    if (!value) {
      return '—'
    }

    return new Intl.DateTimeFormat(tag(), { timeStyle: 'short', hourCycle: 'h23', timeZone: zone() }).format(new Date(value))
  }

  function day(value: string): string {
    return new Intl.DateTimeFormat(tag(), { month: 'numeric', day: 'numeric', timeZone: zone() })
      .format(new Date(`${value}T00:00:00Z`))
  }

  /** Relative time, e.g. "3 minutes ago". */
  function relative(value: string | null | undefined): string {
    if (!value) {
      return '—'
    }

    const seconds = (Date.now() - new Date(value).getTime()) / 1000
    const formatter = new Intl.RelativeTimeFormat(tag(), { numeric: 'auto' })

    const units: [Intl.RelativeTimeFormatUnit, number][] = [
      ['year', 31_536_000],
      ['month', 2_592_000],
      ['day', 86_400],
      ['hour', 3_600],
      ['minute', 60]
    ]

    for (const [unit, size] of units) {
      if (Math.abs(seconds) >= size) {
        return formatter.format(-Math.round(seconds / size), unit)
      }
    }

    return formatter.format(-Math.round(seconds), 'second')
  }

  function duration(seconds: number | null | undefined): string {
    if (seconds === null || seconds === undefined) {
      return '—'
    }

    if (seconds < 60) {
      return t('{seconds} 秒', { seconds: seconds.toFixed(seconds < 10 ? 1 : 0) })
    }

    const minutes = Math.floor(seconds / 60)

    if (minutes < 60) {
      return t('{minutes} 分 {seconds} 秒', { minutes, seconds: Math.round(seconds % 60) })
    }

    return t('{hours} 小時 {minutes} 分', { hours: Math.floor(minutes / 60), minutes: minutes % 60 })
  }

  /** The month/day of an instant (ISO string) in the configured time zone; day() takes YYYY-MM-DD, do not mix the two. */
  function shortDate(value: string | null | undefined): string {
    if (!value) {
      return '—'
    }

    return new Intl.DateTimeFormat(tag(), { month: 'numeric', day: 'numeric', timeZone: zone() }).format(new Date(value))
  }

  return { dateTime, time, day, shortDate, relative, duration }
}
