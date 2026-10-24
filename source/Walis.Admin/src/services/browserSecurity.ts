let restarting = false

/**
 * Replace the current document after authentication changes. Besides returning
 * to a known same-origin route, a real navigation discards every component and
 * Pinia value that may contain data from the previous account.
 */
export function restartApplication(): void {
  if (restarting) {
    return
  }

  restarting = true
  const destination = new URL(window.location.href)

  destination.username = ''
  destination.password = ''
  destination.search = ''
  destination.hash = '/'
  window.location.replace(destination.href)
}
