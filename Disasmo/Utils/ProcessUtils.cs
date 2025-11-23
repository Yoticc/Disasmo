using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Disasmo;

public static class ProcessUtils
{
    public static async Task<ProcessResult> RunProcess(
        string path,
        string args = "", 
        Dictionary<string, string> envVars = null, 
        string workingDirectory = null, 
        Action<bool, string> outputLogger = null, 
        CancellationToken cancellationToken = default)
    {
        UserLogger.Log($"\nExecuting a command in directory \"{workingDirectory}\":\n\t{path} {args}\nEnv.vars:\n{DumpEnvVars(envVars)}");

        var logger = new StringBuilder();
        var loggerForErrors = new StringBuilder();
        Process process = null;
        try
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                Arguments = args,
            };

            if (workingDirectory is not null)
                processStartInfo.WorkingDirectory = workingDirectory;

            if (envVars is not null)
            {
                foreach (var envVar in envVars)
                    processStartInfo.EnvironmentVariables[envVar.Key] = envVar.Value;
            }

            cancellationToken.ThrowIfCancellationRequested();
            process = Process.Start(processStartInfo);
            cancellationToken.ThrowIfCancellationRequested();

            process.ErrorDataReceived += (sender, e) =>
            {
                outputLogger?.Invoke(true, e.Data + "\n");
                logger.AppendLine(e.Data);
                loggerForErrors.AppendLine(e.Data);
            };

            process.OutputDataReceived += (sender, e) =>
            {
                outputLogger?.Invoke(false, e.Data + "\n");
                logger.AppendLine(e.Data);
            };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            cancellationToken.ThrowIfCancellationRequested();
            await process.WaitForExitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            return new ProcessResult { Error = loggerForErrors.ToString().Trim('\r', '\n'), Output = logger.ToString().Trim('\r', '\n') };
        }
        catch (Exception ex)
        {
            workingDirectory ??= Environment.CurrentDirectory;
            var errorMessage = $"RunProcess failed:{ex.Message}.\npath={path}\nargs={args}\nworkingdir={workingDirectory}\n{loggerForErrors}";

            return new ProcessResult { Error = errorMessage };
        }
        finally
        {
            // Just to make sure the process is killed
            process.KillProccessSafe();
        }
    }

    public static Task WaitForExitAsync(this Process process, CancellationToken cancellationToken = default)
    {
        if (process.HasExited) 
            return Task.CompletedTask;

        var completionSource = new TaskCompletionSource<object>();
        process.EnableRaisingEvents = true;
        process.Exited += (sender, args) => completionSource.TrySetResult(null);

        if (cancellationToken != default)
            cancellationToken.Register(() => completionSource.TrySetCanceled());

        return process.HasExited ? Task.CompletedTask : completionSource.Task;
    }

    private static void KillProccessSafe(this Process process)
    {
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private static string DumpEnvVars(Dictionary<string, string> envVars)
    {
        if (envVars is null)
            return string.Empty;

        var envVar = "";
        foreach (var ev in envVars)
            envVar += ev.Key + "=" + ev.Value + "\n";

        return envVar;
    }
}

public class ProcessResult
{
    public string Output { get; set; }
    public string Error { get; set; }
}