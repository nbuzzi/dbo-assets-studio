using System.Diagnostics;

namespace DboAssetsStudio;

internal static class NativeMedia
{
    public static async Task<byte[]> ConvertAsync(string mode, byte[] input, string extension, CancellationToken cancellation = default, string? helperPath = null)
    {
        if (input.Length > 128 * 1024 * 1024) throw new InvalidDataException("El recurso supera el límite de vista previa.");
        string directory = Path.Combine(Path.GetTempPath(), "DboAssetsStudioMedia", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string source = "input" + extension, output = mode == "model" ? "output.mesh" : mode == "audio" ? "output.wav" : "output.json";
            await File.WriteAllBytesAsync(Path.Combine(directory, source), input, cancellation);
            var start = new ProcessStartInfo(helperPath ?? Path.Combine(AppContext.BaseDirectory, "media-helper.exe"))
            { WorkingDirectory = directory, CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(mode); start.ArgumentList.Add(source); start.ArgumentList.Add(output);
            using var process = Process.Start(start) ?? throw new IOException("No se pudo iniciar el lector multimedia.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(45));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync();
                cancellation.ThrowIfCancellationRequested();
                throw new IOException("El lector multimedia excedió el límite de 45 segundos.");
            }
            string details = await stdout + "\n" + await stderr;
            if (process.ExitCode != 0) throw new InvalidDataException("El lector nativo no pudo decodificar este recurso (" + process.ExitCode + ").\n" + details);
            string target = Path.Combine(directory, output);
            if (!File.Exists(target) || new FileInfo(target).Length > 128L * 1024 * 1024) throw new IOException("Salida multimedia inválida o demasiado grande.");
            return await File.ReadAllBytesAsync(target, cancellation);
        }
        finally { Directory.Delete(directory, true); }
    }
}
