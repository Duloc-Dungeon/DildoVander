import { Capacitor, registerPlugin } from '@capacitor/core';

export type DiagnosticExportResult = 'downloaded' | 'shared' | 'cancelled';

interface NativeDiagnosticExport {
  export(options: { fileName: string; contents: string }): Promise<{ status: 'shared' | 'cancelled' }>;
}

const nativeExport = registerPlugin<NativeDiagnosticExport>('KufrDiagnosticExport');

/** Exports a bounded technical JSON object through the platform's normal save/share flow. */
export async function exportDiagnosticReport(
  payload: unknown,
  fileName: string,
): Promise<DiagnosticExportResult> {
  const contents = JSON.stringify(payload, null, 2);
  if (contents === undefined || !/^[A-Za-z0-9][A-Za-z0-9._-]{0,79}\.json$/.test(fileName) || fileName.includes('..') ||
      new TextEncoder().encode(contents).byteLength > 1_048_576) {
    throw new Error('The diagnostic report is invalid or too large to export.');
  }

  if (Capacitor.isNativePlatform()) {
    const result = await nativeExport.export({ fileName, contents });
    if (result.status !== 'shared' && result.status !== 'cancelled') {
      throw new Error('The diagnostic report could not be exported.');
    }
    return result.status;
  }

  const url = URL.createObjectURL(new Blob([contents], { type: 'application/json' }));
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  link.hidden = true;
  document.body.append(link);
  try {
    link.click();
  } finally {
    link.remove();
    window.setTimeout(() => URL.revokeObjectURL(url), 0);
  }
  return 'downloaded';
}
