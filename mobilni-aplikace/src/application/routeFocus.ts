export function focusRouteContent(content: HTMLElement | undefined): void {
  // Navigation announcements run in a later frame. Preserve focus the visitor
  // has already moved into the new content before that callback runs.
  if (!content || content.contains(document.activeElement)) return;
  content.focus({ preventScroll: true });
}
