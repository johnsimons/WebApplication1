using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Text;

namespace WebApplication1.Controllers
{
    public class CmdResult
    {
        readonly List<string> infos;
        readonly List<string> errors;

        public CmdResult(int exitCode, List<string> infos, List<string> errors)
        {
            ExitCode = exitCode;
            this.infos = infos;
            this.errors = errors;
        }

        public int ExitCode { get; }

        public IList<string> Infos => infos;

        public IList<string> Errors => errors;

        public void Validate()
        {
            if (ExitCode != 0)
            {
                throw new CommandLineException(ExitCode, errors);
            }
        }
    }

    public class CommandLineException : Exception
    {
        readonly int exitCode;

        public CommandLineException(int exitCode, List<string> errors)
        {
            this.exitCode = exitCode;
            Errors = errors;
        }

        public List<string> Errors { get; }

        public override string Message
        {
            get
            {
                var sb = new StringBuilder(base.Message);

                sb.AppendFormat(" Exit code: {0}", exitCode);
                if (Errors.Count > 0)
                {
                    sb.AppendLine(string.Join(Environment.NewLine, Errors));
                }

                return sb.ToString();
            }
        }
    }
    
    public class CommandLineInvocation
    {
        public CommandLineInvocation(string executable, string arguments, string? systemArguments = null)
        {
            Executable = executable;
            Arguments = arguments;
            SystemArguments = systemArguments;
        }

        public string Executable { get; }

        public string Arguments { get; }

        // Arguments only used when we are invoking this directly from within the tools - not used when 
        // exporting the script for use later.
        public string? SystemArguments { get; }

        public bool IgnoreFailedExitCode { get; set; }

        public override string ToString() => "\"" + Executable + "\" " + Arguments;
    }
    
    public static class SilentProcessRunner
    {
        static readonly object EnvironmentVariablesCacheLock = new();
        static IDictionary mostRecentMachineEnvironmentVariables = Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Machine);
        static readonly Dictionary<string, Dictionary<string, string>> EnvironmentVariablesForUserCache = new();

        public static CmdResult ExecuteCommand(this CommandLineInvocation invocation) => ExecuteCommand(invocation, Environment.CurrentDirectory);

        public static CmdResult ExecuteCommand(this CommandLineInvocation invocation, string workingDirectory)
        {
            if (workingDirectory == null)
            {
                throw new ArgumentNullException(nameof(workingDirectory));
            }

            var arguments = $"{invocation.Arguments} {invocation.SystemArguments ?? string.Empty}";
            var infos = new List<string>();
            var errors = new List<string>();

            var exitCode = ExecuteCommand(
                invocation.Executable,
                arguments,
                workingDirectory,
                infos.Add,
                errors.Add
            );

            return new CmdResult(exitCode, infos, errors);
        }

        public static int ExecuteCommand(
            string executable,
            string arguments,
            string workingDirectory,
            Action<string> info,
            Action<string> error,
            IDictionary<string, string>? customEnvironmentVariables = null,
            CancellationToken cancel = default) =>
            ExecuteCommand(
                executable,
                arguments,
                workingDirectory,
                s => {},
                info,
                error,
                customEnvironmentVariables,
                cancel);

        public static int ExecuteCommand(
            string executable,
            string arguments,
            string workingDirectory,
            Action<string> debug,
            Action<string> info,
            Action<string> error,
            IDictionary<string, string>? customEnvironmentVariables = null,
            CancellationToken cancel = default)
        {
            if (executable == null)
            {
                throw new ArgumentNullException(nameof(executable));
            }

            if (arguments == null)
            {
                throw new ArgumentNullException(nameof(arguments));
            }

            if (workingDirectory == null)
            {
                throw new ArgumentNullException(nameof(workingDirectory));
            }

            if (debug == null)
            {
                throw new ArgumentNullException(nameof(debug));
            }

            if (info == null)
            {
                throw new ArgumentNullException(nameof(info));
            }

            if (error == null)
            {
                throw new ArgumentNullException(nameof(error));
            }

            customEnvironmentVariables = customEnvironmentVariables == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(customEnvironmentVariables);

            void WriteData(Action<string> action, ManualResetEventSlim resetEvent, DataReceivedEventArgs e)
            {
                try
                {
                    if (e.Data == null)
                    {
                        resetEvent.Set();
                        return;
                    }

                    action(e.Data);
                }
                catch (Exception ex)
                {
                    try
                    {
                        error($"Error occurred handling message: {ex}");
                    }
                    catch
                    {
                        // Ignore
                    }
                }
            }

            try
            {
                // We need to be careful to make sure the message is accurate otherwise people could wrongly assume the exe is in the working directory when it could be somewhere completely different!
                var executableDirectoryName = Path.GetDirectoryName(executable);
                debug($"Executable directory is {executableDirectoryName}");

                var exeInSamePathAsWorkingDirectory = string.Equals(executableDirectoryName?.TrimEnd('\\', '/'), workingDirectory.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
                var exeFileNameOrFullPath = exeInSamePathAsWorkingDirectory ? Path.GetFileName(executable) : executable;
                debug($"Executable name or full path: {exeFileNameOrFullPath}");

                using (var outputResetEvent = new ManualResetEventSlim(false))
                using (var errorResetEvent = new ManualResetEventSlim(false))
                using (var process = new Process())
                {
                    process.StartInfo.FileName = executable;
                    process.StartInfo.Arguments = arguments;
                    process.StartInfo.WorkingDirectory = workingDirectory;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;

                    RunAsSameUser(process.StartInfo, customEnvironmentVariables);

                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    
                    process.OutputDataReceived += (sender, e) =>
                    {
                        WriteData(info, outputResetEvent, e);
                    };

                    process.ErrorDataReceived += (sender, e) =>
                    {
                        WriteData(error, errorResetEvent, e);
                    };

                    process.Start();

                    using (cancel.Register(
                               () =>
                               {
                                   
                               }))
                    {
                        

                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();

                        process.WaitForExit();

                        SafelyCancelRead(process.CancelErrorRead, debug);
                        SafelyCancelRead(process.CancelOutputRead, debug);

                        SafelyWaitForAllOutput(outputResetEvent, cancel, debug);
                        SafelyWaitForAllOutput(errorResetEvent, cancel, debug);

                        var exitCode = SafelyGetExitCode(process);
                        debug($"Process {exeFileNameOrFullPath} in {workingDirectory} exited with code {exitCode}");

                        return exitCode;
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error when attempting to execute {executable}: {ex.Message}", ex);
            }
        }

        static int SafelyGetExitCode(Process process)
        {
            try
            {
                return process.ExitCode;
            }
            catch (InvalidOperationException ex)
                when (ex.Message == "No process is associated with this object." || ex.Message == "Process was not started by this object, so requested information cannot be determined.")
            {
                return -1;
            }
        }

        static void SafelyWaitForAllOutput(ManualResetEventSlim outputResetEvent,
            CancellationToken cancel,
            Action<string> debug)
        {
            try
            {
                //5 seconds is a bit arbitrary, but the process should have already exited by now, so unwise to wait too long
                outputResetEvent.Wait(TimeSpan.FromSeconds(5), cancel);
            }
            catch (OperationCanceledException ex)
            {
                debug($"Swallowing {ex.GetType().Name} while waiting for last of the process output.");
            }
        }

        static void SafelyCancelRead(Action action, Action<string> debug)
        {
            try
            {
                action();
            }
            catch (InvalidOperationException ex)
            {
                debug($"Swallowing {ex.GetType().Name} calling {action.Method.Name}.");
            }
        }

        public static void ExecuteCommandWithoutWaiting(
            string executable,
            string arguments,
            string workingDirectory,
            NetworkCredential? runAs = null,
            IDictionary<string, string>? customEnvironmentVariables = null)
        {
            customEnvironmentVariables = customEnvironmentVariables == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(customEnvironmentVariables);

            try
            {
                using (var process = new Process())
                {
                    process.StartInfo.FileName = executable;
                    process.StartInfo.Arguments = arguments;
                    process.StartInfo.WorkingDirectory = workingDirectory;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;

                    if (runAs == null)
                    {
                        RunAsSameUser(process.StartInfo, customEnvironmentVariables);
                    }
                    else
                    {
                        throw new PlatformNotSupportedException("NetCore on linux or Mac does not support running a process as a different user.");
                    }

                    process.Start();
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error when attempting to execute {executable}: {ex.Message}", ex);
            }
        }

        static void RunAsSameUser(ProcessStartInfo processStartInfo, IDictionary<string, string>? customEnvironmentVariables)
        {
            // Accessing the ProcessStartInfo.EnvironmentVariables dictionary will pre-load the environment variables for the current process
            // Then we'll add/overwrite with the customEnvironmentVariables
            if (customEnvironmentVariables != null && customEnvironmentVariables.Any())
            {
                foreach (var variable in customEnvironmentVariables)
                {
                    processStartInfo.EnvironmentVariables[variable.Key] = variable.Value;
                }
            }
        }
    }
}
