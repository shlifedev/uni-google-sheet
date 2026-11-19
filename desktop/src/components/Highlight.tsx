/** The text with every case-insensitive occurrence of the query marked. Plain text when there is no query. */
export function Highlight({ text, query }: { text: string; query: string }) {
  if (!query) return <>{text}</>;
  const lower = text.toLowerCase();
  const needle = query.toLowerCase();
  let at = lower.indexOf(needle);
  if (at < 0) return <>{text}</>;

  const parts: React.ReactNode[] = [];
  let start = 0;
  while (at >= 0) {
    if (at > start) parts.push(text.slice(start, at));
    parts.push(<mark key={at}>{text.slice(at, at + needle.length)}</mark>);
    start = at + needle.length;
    at = lower.indexOf(needle, start);
  }
  if (start < text.length) parts.push(text.slice(start));
  return <>{parts}</>;
}
