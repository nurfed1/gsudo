﻿using gsudo.Helpers;
using gsudo.Native;
using gsudo.ProcessRenderers;
using gsudo.Rpc;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;

namespace gsudo.Commands
{
    public class RunCommand : ICommand
    {
        public IList<string> CommandToRun { get; private set; }
        private string GetArguments() => GetArgumentsString(CommandToRun, 1);

        public RunCommand(IList<string> commandToRun)
        {
            CommandToRun = commandToRun;
        }

        public async Task<int> Execute()
        {
            if (InputArguments.IntegrityLevel == IntegrityLevel.System && !InputArguments.RunAsSystem)
            {
                Logger.Instance.Log($"Elevating as System because of IntegrityLevel=System parameter.", LogLevel.Warning);
                InputArguments.RunAsSystem = true;
            }

            bool isRunningAsDesiredUser = IsRunningAsDesiredUser();
            bool isElevationRequired = IsElevationRequired();
            bool isShellElevation = !CommandToRun.Any(); // are we auto elevating the current shell?

            if (isElevationRequired & ProcessHelper.GetCurrentIntegrityLevel() < (int)IntegrityLevel.Medium)
                throw new ApplicationException("Sorry, gsudo doesn't allow to elevate from low integrity level."); // This message is not a security feature, but a nicer error message. It would have failed anyway since the named pipe's ACL restricts it.

            if (isRunningAsDesiredUser && isShellElevation && !InputArguments.NewWindow)
                throw new ApplicationException("Already running as the specified user/permission-level (and no command specified). Exiting...");

            CommandToRun = CommandToRunGenerator.AugmentCommand(CommandToRun.ToArray());

            bool isWindowsApp = ProcessFactory.IsWindowsApp(CommandToRun.FirstOrDefault());
            var elevationMode = GetElevationMode();

            CommandToRun = CommandToRunGenerator.FixCommandExceptions(CommandToRun);

            if (!isRunningAsDesiredUser)
                CommandToRun = CommandToRunGenerator.AddCopyEnvironment(CommandToRun, elevationMode);

            var exeName = CommandToRun.FirstOrDefault();

            int consoleHeight, consoleWidth;
            ConsoleHelper.GetConsoleInfo(out consoleWidth, out consoleHeight, out _, out _);

            var elevationRequest = new ElevationRequest()
            {
                FileName = exeName,
                Arguments = GetArguments(),
                StartFolder = Environment.CurrentDirectory,
                NewWindow = InputArguments.NewWindow,
                Wait = (!isWindowsApp && !InputArguments.NewWindow) || InputArguments.Wait,
                Mode = elevationMode,
                ConsoleProcessId = Process.GetCurrentProcess().Id,
                IntegrityLevel = InputArguments.GetIntegrityLevel(),
                ConsoleWidth = consoleWidth,
                ConsoleHeight = consoleHeight,
                IsInputRedirected = Console.IsInputRedirected
            };

            if (isElevationRequired && Settings.SecurityEnforceUacIsolation)
                AdjustUacIsolationRequest(elevationRequest, isShellElevation);

            SetRequestPrompt(elevationRequest);

            Logger.Instance.Log($"Command to run: {elevationRequest.FileName} {elevationRequest.Arguments}", LogLevel.Debug);

            if (isRunningAsDesiredUser || !isElevationRequired) // already elevated or running as correct user. No service needed.
            {
                return RunWithoutService(exeName, GetArguments(), elevationRequest);
            }

            return await RunUsingService(elevationRequest).ConfigureAwait(false);
        }

        private static void SetRequestPrompt(ElevationRequest elevationRequest)
        {
            if ((int)InputArguments.GetIntegrityLevel() < (int)IntegrityLevel.High)
                elevationRequest.Prompt = Environment.GetEnvironmentVariable("PROMPT", EnvironmentVariableTarget.User) ?? Environment.GetEnvironmentVariable("PROMPT", EnvironmentVariableTarget.Machine) ?? "$P$G";
            else if (elevationRequest.Mode != ElevationRequest.ConsoleMode.Piped || InputArguments.NewWindow)
                elevationRequest.Prompt = Settings.Prompt;
            else
                elevationRequest.Prompt = Settings.PipedPrompt;
        }

        /// Starts a cache sessioBn
        private async Task<int> RunUsingService(ElevationRequest elevationRequest)
        {
            Logger.Instance.Log($"Using Console mode {elevationRequest.Mode}", LogLevel.Debug);

            var cmd = CommandToRun.FirstOrDefault();

            Rpc.Connection connection = null;
            try
            {
                var callingPid = ProcessHelper.GetCallerPid();

                Logger.Instance.Log($"Caller PID: {callingPid}", LogLevel.Debug);

                connection = await ServiceHelper.Connect(null).ConfigureAwait(false);

                if (connection == null) // service is not running or listening.
                {
                    ServiceHelper.StartService(callingPid, singleUse: InputArguments.KillCache);
                    connection = await ServiceHelper.Connect(callingPid).ConfigureAwait(false);

                    if (connection == null) // service is not running or listening.
                        throw new ApplicationException("Unable to connect to the elevated service.");
                }

                var renderer = GetRenderer(connection, elevationRequest);
                await connection.WriteElevationRequest(elevationRequest).ConfigureAwait(false);
                ConnectionKeepAliveThread.Start(connection);

                var exitCode = await renderer.Start().ConfigureAwait(false);
                Logger.Instance.Log($"Process exited with code {exitCode}", LogLevel.Debug);

                return exitCode;
            }
            finally
            {
                connection?.Dispose();
            }
        }

        private static int RunWithoutService(string exeName, string args, ElevationRequest elevationRequest)
        {
            var sameIntegrity = (int)InputArguments.GetIntegrityLevel() == ProcessHelper.GetCurrentIntegrityLevel();
            // No need to escalate. Run in-process
            Native.ConsoleApi.SetConsoleCtrlHandler(ConsoleHelper.IgnoreConsoleCancelKeyPress, true);

            if (!string.IsNullOrEmpty(elevationRequest.Prompt))
            {
                Environment.SetEnvironmentVariable("PROMPT", Environment.ExpandEnvironmentVariables(elevationRequest.Prompt));
            }

            if (sameIntegrity)
            {
                if (elevationRequest.NewWindow)
                {
                    using (var process = ProcessFactory.StartDetached(exeName, args, Environment.CurrentDirectory, false))
                    {
                        if (elevationRequest.Wait)
                        {
                            process.WaitForExit();
                            var exitCode = process.ExitCode;
                            Logger.Instance.Log($"Process exited with code {exitCode}", LogLevel.Debug);
                            return exitCode;
                        }
                        return 0;
                    }
                }
                else
                {
                    using (Process process = ProcessFactory.StartAttached(exeName, args))
                    {
                        process.WaitForExit();
                        var exitCode = process.ExitCode;
                        Logger.Instance.Log($"Process exited with code {exitCode}", LogLevel.Debug);
                        return exitCode;
                    }
                }
            }
            else // lower integrity
            {
                if (elevationRequest.IntegrityLevel<IntegrityLevel.High && !elevationRequest.NewWindow)
                    RemoveAdminPrefixFromConsoleTitle();

                var p = ProcessFactory.StartAttachedWithIntegrity(InputArguments.GetIntegrityLevel(), exeName, args, elevationRequest.StartFolder, InputArguments.NewWindow, !InputArguments.NewWindow);
                if (p == null || p.IsInvalid)
                    return Constants.GSUDO_ERROR_EXITCODE;

                if (elevationRequest.Wait)
                {
                    ProcessHelper.GetProcessWaitHandle(p.DangerousGetHandle()).WaitOne();
                    ProcessApi.GetExitCodeProcess(p, out var exitCode);
                    Logger.Instance.Log($"Process exited with code {exitCode}", LogLevel.Debug);
                    return exitCode;
                }

                return 0;
            }
        }

        // Enforce SecurityEnforceUacIsolation
        private void AdjustUacIsolationRequest(ElevationRequest elevationRequest, bool isShellElevation)
        {
            if ((int)(InputArguments.GetIntegrityLevel()) >= ProcessHelper.GetCurrentIntegrityLevel())
            {
                if (!elevationRequest.NewWindow)
                {
                    if (isShellElevation)
                    {
                        // force auto shell elevation in new window
                        elevationRequest.NewWindow = true;
                        // do not wait by default on this scenario, only if user has requested it.
                        elevationRequest.Wait = InputArguments.Wait;
                        Logger.Instance.Log("Elevating shell in a new console window because of SecurityEnforceUacIsolation", LogLevel.Info);
                    }
                    else
                    {
                        // force raw mode (that disables user input with SecurityEnforceUacIsolation)
                        elevationRequest.Mode = ElevationRequest.ConsoleMode.Piped;
                        Logger.Instance.Log("User Input disabled because of SecurityEnforceUacIsolation. Press Ctrl-C three times to abort. Or use -n argument to elevate in new window.", LogLevel.Info);
                    }
                }
            }
        }

        internal static bool IsRunningAsDesiredUser()
        {
            if (InputArguments.TrustedInstaller && !WindowsIdentity.GetCurrent().Claims.Any(c => c.Value == Constants.TI_SID))
                return false;

            if (InputArguments.RunAsSystem && !WindowsIdentity.GetCurrent().IsSystem)
                return false;

            if ((int)InputArguments.GetIntegrityLevel() != ProcessHelper.GetCurrentIntegrityLevel())
                return false;

            if (!string.IsNullOrEmpty(InputArguments.UserName) && InputArguments.UserName != WindowsIdentity.GetCurrent().Owner.ToString())
                return false;

            return true;
        }

        private static bool IsElevationRequired()
        {
            if (InputArguments.TrustedInstaller && !WindowsIdentity.GetCurrent().Claims.Any(c => c.Value == Constants.TI_SID))
                return true;

            if (InputArguments.RunAsSystem && !WindowsIdentity.GetCurrent().IsSystem)
                return true;

            var integrityLevel = InputArguments.GetIntegrityLevel();

            if (integrityLevel == IntegrityLevel.MediumRestricted)
                return true;

            if (!string.IsNullOrEmpty(InputArguments.UserName) && (integrityLevel < IntegrityLevel.High))
                return true;

            return (int)integrityLevel > ProcessHelper.GetCurrentIntegrityLevel();
        }

        /// <summary>
        /// Decide wheter we will use raw piped I/O screen communication, 
        /// or enhanced, colorfull VT mode with nice TAB auto-complete.
        /// </summary>
        /// <returns></returns>
        private static ElevationRequest.ConsoleMode GetElevationMode()
        {
            if ((!ProcessHelper.IsMemberOfLocalAdmins() || // => Not local admin? Force attached mode, so the new process has admin user env vars. (See #113)
                Settings.ForceAttachedConsole) && !Settings.ForcePipedConsole && !Settings.ForceVTConsole)
            {
                if (Console.IsErrorRedirected
                    || Console.IsInputRedirected
                    || Console.IsOutputRedirected)
                {
                    // Attached mode doesnt supports redirection.
                    return ElevationRequest.ConsoleMode.Piped; 
                }
                if (InputArguments.TrustedInstaller)
                    return ElevationRequest.ConsoleMode.VT; // workaround for #173

                return ElevationRequest.ConsoleMode.Attached;
            }

            if (Settings.ForcePipedConsole)
                return ElevationRequest.ConsoleMode.Piped;

            if (Settings.ForceVTConsole)
            {
                if (Console.IsErrorRedirected && Console.IsOutputRedirected)
                {
                    // VT mode (i.e. Windows Pseudoconsole) arguably is not a good fit
                    // for redirection/capturing: output contains VT codes, which means:
                    // cursor positioning, colors, etc.

                    // Nonetheless I will allow redirection of one of Err/Out, for now.
                    // (not if both are redirected it breaks badly because there are two
                    // streams trying to use one single console.)

                    return ElevationRequest.ConsoleMode.Piped;
                }

                return ElevationRequest.ConsoleMode.VT;
            }

            return ElevationRequest.ConsoleMode.TokenSwitch;
        }

        private static IProcessRenderer GetRenderer(Connection connection, ElevationRequest elevationRequest)
        {
            if (elevationRequest.Mode == ElevationRequest.ConsoleMode.TokenSwitch)
            {
                try
                {
                    return new TokenSwitchRenderer(connection, elevationRequest);
                }
                catch (Exception ex)
                {
                    Logger.Instance.Log($"TokenSwitchRenderer mode failed with {ex.ToString()}. Fallback to Attached Mode", LogLevel.Debug);
                    elevationRequest.Mode = ElevationRequest.ConsoleMode.Attached; // fallback to attached mode.
                    return new AttachedConsoleRenderer(connection);
                }
            }
            if (elevationRequest.Mode == ElevationRequest.ConsoleMode.Attached)
                return new AttachedConsoleRenderer(connection);
            if (elevationRequest.Mode == ElevationRequest.ConsoleMode.Piped)
                return new PipedClientRenderer(connection);
            else
                return new VTClientRenderer(connection, elevationRequest);
        }

        private static string GetArgumentsString(IEnumerable<string> args, int v)
        {
            if (args == null) return null;
            if (args.Count() <= v) return string.Empty;
            return string.Join(" ", args.Skip(v).ToArray());
        }

        private static void RemoveAdminPrefixFromConsoleTitle()
        {
            var title = Console.Title;
            var colonPos = title.IndexOf(":", StringComparison.InvariantCulture);
            if (colonPos > 1) // no accidental modifying of "C:\..."
                Console.Title = title.Substring(colonPos+1).TrimStart();
        }          
    }
}
