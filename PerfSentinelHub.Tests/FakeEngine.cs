namespace PerfSentinelHub.Tests;

/// <summary>
///     A stand-in perf-sentinel binary: a <c>/bin/sh</c> script on Unix, a batch file on
///     Windows. Both only dispatch on the subcommand. What they print lives in files beside
///     them, so no payload goes through either shell's quoting.
/// </summary>
internal sealed record FakeEngine
{
    /// <summary>Printed for <c>--version</c>. Null runs <c>--version</c> as a query.</summary>
    public string? Version { get; init; }

    public int VersionExitCode { get; init; }

    /// <summary>Printed for <c>report --help</c>, which the probe reads for <c>--daemon-url</c>.</summary>
    public string Help { get; init; } = "";

    public int HelpExitCode { get; init; }

    /// <summary>What the query subcommand prints: anything but <c>--version</c> and <c>report</c>.</summary>
    public string Output { get; init; } = "";

    public string Error { get; init; } = "";

    public int ExitCode { get; init; }

    public int SleepSeconds { get; init; }

    /// <summary>
    ///     Writes the engine at <paramref name="path" /> and returns what to launch, which on
    ///     Windows carries a <c>.cmd</c> extension. <c>report</c> records its arguments in
    ///     <c>render-args.txt</c> in its working directory and writes an HTML file wherever
    ///     <c>--output</c> points, the two-step shape the real binary imposes.
    /// </summary>
    public string WriteTo(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText($"{path}.version", Version ?? "");
        File.WriteAllText($"{path}.help", Help);
        File.WriteAllText($"{path}.out", Output);
        File.WriteAllText($"{path}.err", Error);
        File.WriteAllText($"{path}.html", "<html>report</html>");

        if (OperatingSystem.IsWindows())
        {
            // CRLF, because cmd.exe can miss a label in a file with bare LF line endings.
            File.WriteAllText($"{path}.cmd", BatchScript(path).ReplaceLineEndings("\r\n"));
            return $"{path}.cmd";
        }

        // LF, because a CRLF checkout would otherwise hand sh an interpreter named "/bin/sh\r".
        File.WriteAllText(path, ShellScript(path).ReplaceLineEndings("\n"));
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private string ShellScript(string path)
    {
        var version = Version is null
            ? ""
            : $"""if [ "$1" = "--version" ]; then cat "$P.version"; exit {VersionExitCode}; fi""";
        return $"""
                #!/bin/sh
                P={Quote(path)}
                {version}
                if [ "$1" = "report" ] && [ "$2" = "--help" ]; then cat "$P.help"; exit {HelpExitCode}; fi
                if [ "$1" = "report" ]; then
                  echo "$@" > render-args.txt
                  while [ $# -gt 0 ]; do
                    if [ "$1" = "--output" ]; then shift; cat "$P.html" > "$1"; fi
                    shift
                  done
                  exit 0
                fi
                sleep {SleepSeconds}
                cat "$P.err" >&2
                cat "$P.out"
                exit {ExitCode}

                """;
    }

    private string BatchScript(string path)
    {
        var version = Version is null
            ? ""
            : $"""if "%~1"=="--version" (type "%P%.version" & exit /b {VersionExitCode})""";
        // ping stands in for sleep: timeout refuses to run once stdin is redirected.
        var sleep = SleepSeconds > 0 ? $"ping -n {SleepSeconds + 1} 127.0.0.1 >nul" : "";
        return $"""
                @echo off
                set "P={path}"
                {version}
                if "%~1"=="report" if "%~2"=="--help" (type "%P%.help" & exit /b {HelpExitCode})
                if not "%~1"=="report" goto query
                >render-args.txt echo %*
                :next
                if "%~1"=="" exit /b 0
                if "%~1"=="--output" copy /y "%P%.html" "%~2" >nul
                shift
                goto next
                :query
                {sleep}
                type "%P%.err" 1>&2
                type "%P%.out"
                exit /b {ExitCode}

                """;
    }

    private static string Quote(string value)
    {
        return $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
    }
}
