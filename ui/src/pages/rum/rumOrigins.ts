export function parseOrigins(value: string): string[] | null {
  const lines = value.split(/\r?\n/).map((line) => line.trim()).filter(Boolean);
  const origins: string[] = [];
  for (const line of lines) {
    try {
      const url = new URL(line);
      if (!/^https?:\/\/[^/?#\\\s]+\/?$/i.test(line) || url.username || url.password ||
        url.hostname.includes('*') || line.includes(',') || !['http:', 'https:'].includes(url.protocol)) return null;
      origins.push(url.origin);
    } catch {
      return null;
    }
  }
  return [...new Set(origins)];
}

export function displayOrigins(json: string): string {
  try {
    const values: unknown = JSON.parse(json);
    return Array.isArray(values) && values.every((item) => typeof item === 'string') ? values.join('\n') : json;
  } catch {
    return json;
  }
}
