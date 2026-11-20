/**
 * 搜索结果 CSV 导出（HyperDX 式下载按钮）。
 * RFC 4180 引号转义 + CRLF 行尾 + UTF-8 BOM（Excel 直开中文不乱码）。
 */

/** 单元格序列化：null/undefined → 空串，含逗号/引号/换行时加引号并转义内部引号. */
function escapeCell(value: unknown): string {
  if (value === null || value === undefined) {
    return '';
  }

  const text = typeof value === 'object' ? JSON.stringify(value) : String(value);
  return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
}

/** 二维结果 → CSV 文本（首行列名，CRLF 行尾，无 BOM）. */
export function toCsv(columns: string[], rows: unknown[][]): string {
  const lines = [columns.map(escapeCell).join(','), ...rows.map((row) => row.map(escapeCell).join(','))];
  return lines.join('\r\n');
}

/** 触发浏览器下载 CSV 文件（带 BOM，文件名 timeRange 可选）. */
export function downloadCsv(columns: string[], rows: unknown[][], fileName = 'search-results.csv'): void {
  const blob = new Blob([`\uFEFF${toCsv(columns, rows)}`], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = fileName;
  anchor.click();
  URL.revokeObjectURL(url);
}
