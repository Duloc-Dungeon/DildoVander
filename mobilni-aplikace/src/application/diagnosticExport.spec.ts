import { beforeEach, describe, expect, it, vi } from 'vitest';

const native = vi.hoisted(() => ({ enabled: false, export: vi.fn() }));

vi.mock('@capacitor/core', () => ({
  Capacitor: { isNativePlatform: () => native.enabled },
  registerPlugin: () => ({ export: native.export }),
}));

import { diagnosticExportHistory, exportDiagnosticReport } from './diagnosticExport';

describe('diagnostic report export', () => {
  beforeEach(() => {
    native.enabled = false;
    native.export.mockReset();
  });

  it('downloads valid JSON in the browser', async () => {
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    const create = vi.spyOn(URL, 'createObjectURL').mockImplementation(blob => {
      expect(blob).toBeInstanceOf(Blob);
      if (!(blob instanceof Blob)) throw new Error('Expected a JSON Blob.');
      expect(blob.type).toBe('application/json');
      return 'blob:diagnostics';
    });
    const revoke = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});

    await expect(exportDiagnosticReport({ schemaVersion: 2, requests: [{ outcome: 'success' }] }, 'report.json'))
      .resolves.toBe('downloaded');

    expect(create).toHaveBeenCalledOnce();
    expect(click).toHaveBeenCalledOnce();
    await vi.waitFor(() => expect(revoke).toHaveBeenCalledWith('blob:diagnostics'));
    click.mockRestore(); create.mockRestore(); revoke.mockRestore();
  });

  it('uses native sharing and reports cancellation without claiming a save', async () => {
    native.enabled = true;
    native.export.mockResolvedValue({ status: 'cancelled' });
    await expect(exportDiagnosticReport({ requests: [] }, 'report.json')).resolves.toBe('cancelled');
    expect(native.export).toHaveBeenCalledWith({ fileName: 'report.json', contents: '{\n  "requests": []\n}' });
  });

  it('surfaces native file or share failures to the caller', async () => {
    native.enabled = true;
    native.export.mockRejectedValue(new Error('file_write_failed'));
    await expect(exportDiagnosticReport({ requests: [] }, 'report.json')).rejects.toThrow('file_write_failed');
  });

  it('retains bounded technical export failure evidence without private error messages or report contents', async () => {
    native.enabled = true;
    native.export.mockRejectedValue(Object.assign(new Error('PRIVATE-PATH-AND-CONTENT'), { code: 'diagnostic_export_write_failed' }));
    await expect(exportDiagnosticReport({ secret: 'PRIVATE-REPORT' }, 'report.json')).rejects.toThrow();
    expect(diagnosticExportHistory().at(-1)).toMatchObject({ platform: 'native', outcome: 'failed', code: 'diagnostic_export_write_failed' });
    expect(JSON.stringify(diagnosticExportHistory())).not.toContain('PRIVATE');
    expect(JSON.stringify(diagnosticExportHistory())).not.toContain('report.json');
    native.export.mockResolvedValue({ status: 'cancelled' });
    for (let index = 0; index < 25; index++) await exportDiagnosticReport({}, 'report.json');
    expect(diagnosticExportHistory()).toHaveLength(20);
    expect(diagnosticExportHistory().at(-1)?.outcome).toBe('cancelled');
  });

  it('rejects unsafe filenames and oversized reports before opening a platform export', async () => {
    await expect(exportDiagnosticReport({}, '../report.json')).rejects.toThrow('invalid or too large');
    await expect(exportDiagnosticReport({ value: 'x'.repeat(1_048_577) }, 'report.json')).rejects.toThrow('invalid or too large');
    expect(native.export).not.toHaveBeenCalled();
  });

  it('exports only the supplied sanitized technical report', async () => {
    native.enabled = true;
    native.export.mockResolvedValue({ status: 'shared' });
    const report = { schemaVersion: 2, requestIds: ['safe-id'], outcomes: ['http_error'] };
    await exportDiagnosticReport(report, 'report.json');
    const contents = native.export.mock.calls[0][0].contents;
    expect(JSON.parse(contents)).toEqual(report);
    for (const privateField of ['transcript', 'apiKey', 'authorization', 'cookie', 'memory', 'image']) {
      expect(contents).not.toContain(privateField);
    }
  });
});
