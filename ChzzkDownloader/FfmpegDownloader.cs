using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ChzzkDownloader
{
    internal static class FfmpegDownloader
    {
        private static readonly Regex DurationPattern = new Regex(
            @"Duration:\s*(\d+:\d{2}:\d{2}(?:\.\d+)?)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static string Quote(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }

        private static string FfmpegPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe"); }
        }

        // The FFmpeg executable and output folder are anchored to the application's directory.
        internal static async Task DownloadAsync(string url, TimeSpan start, TimeSpan end,
            string outputPath, IProgress<double> progress, IProgress<string> phase,
            Action<string> log, CancellationToken cancellationToken)
        {
            if (!File.Exists(FfmpegPath))
                throw new FileNotFoundException(
                    "실행 파일과 같은 폴더에 ffmpeg.exe가 있어야 합니다.", FfmpegPath);
            if (end <= start)
                throw new ArgumentOutOfRangeException("end", "종료 시간은 시작 시간보다 커야 합니다.");

            string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output");
            Directory.CreateDirectory(folder);
            string output = Path.Combine(folder, Path.GetFileName(outputPath));
            string partial = Path.Combine(folder,
                Path.GetFileNameWithoutExtension(output) + ".partial.mp4");
            if (File.Exists(partial))
                throw new IOException("임시 파일이 남아 있습니다. 확인 후 삭제해 주세요: " + partial);

            // Both -ss and -to MUST be input options, before -i, to refer to source timestamps.
            string args = "-hide_banner -nostdin -y -loglevel warning " +
                          "-stats_period 0.5 -progress pipe:1 " +
                          "-ss " + TimeCode.Format(start) + " " +
                          "-to " + TimeCode.Format(end) + " " +
                          "-i " + Quote(url) + " -map 0:v:0 -map 0:a? " +
                          "-c copy -movflags +faststart " + Quote(partial);
            bool committed = false;
            try
            {
                phase.Report("다운로드 중...");
                log("다운로드 시작: " + TimeCode.Format(start) + " ~ " + TimeCode.Format(end));
                double expectedSeconds = (end - start).TotalSeconds;
                await RunProcessAsync(args, expectedSeconds, progress,
                    cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(partial) || new FileInfo(partial).Length < 1024)
                    throw new InvalidOperationException("생성된 동영상 파일이 없거나 너무 작습니다.");

                phase.Report("파일 검증 중... (FFmpeg로 영상 패킷과 길이를 확인합니다)");
                log("다운로드 파일 검증 시작");
                await VerifyFileAsync(partial, end - start, cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(output)) File.Delete(output);
                File.Move(partial, output);
                committed = true;
                log("파일 검증 완료 · 저장 성공: " + output);
            }
            finally
            {
                if (!committed && File.Exists(partial))
                {
                    try { File.Delete(partial); }
                    catch (IOException) { log("임시 파일 삭제 실패: " + partial); }
                    catch (UnauthorizedAccessException) { log("임시 파일 삭제 권한 없음: " + partial); }
                }
            }
        }

        private static async Task RunProcessAsync(string arguments, double expectedSeconds,
            IProgress<double> progress, CancellationToken token)
        {
            var diagnostics = new StringBuilder();
            var info = MakeStartInfo(arguments);
            using (var process = new Process { StartInfo = info })
            {
                process.OutputDataReceived += (sender, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data) || !e.Data.StartsWith("out_time="))
                        return;
                    TimeSpan position;
                    if (TimeSpan.TryParse(e.Data.Substring("out_time=".Length),
                        CultureInfo.InvariantCulture, out position) && expectedSeconds > 0)
                    {
                        // 100% is reserved for a verified output file.
                        progress.Report(Math.Min(99, Math.Max(0,
                            position.TotalSeconds * 100.0 / expectedSeconds)));
                    }
                };
                process.ErrorDataReceived += (sender, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    lock (diagnostics) AppendDiagnostic(diagnostics, e.Data);
                };
                await RunProcessAndWaitAsync(process, token).ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    string details;
                    lock (diagnostics) details = diagnostics.ToString().Trim();
                    throw new InvalidOperationException("FFmpeg 다운로드 실패 (종료 코드 " +
                        process.ExitCode + ")." +
                        (details.Length == 0 ? "" : Environment.NewLine + details));
                }
            }
        }

        private static async Task VerifyFileAsync(string path, TimeSpan target, CancellationToken token)
        {
            // Scan ALL video packets, not just the MP4 header/size. Copying to NUL
            // keeps verification fast and requires no separate ffprobe.exe.
            string args = "-hide_banner -nostdin -loglevel info -xerror " +
                          "-err_detect explode -progress pipe:1 -i " + Quote(path) +
                          " -map 0:v:0 -c copy -f null NUL";
            var diagnostics = new StringBuilder();
            double? headerSeconds = null;
            double packetSeconds = 0;
            bool scannedPackets = false;
            using (var process = new Process { StartInfo = MakeStartInfo(args) })
            {
                process.OutputDataReceived += (sender, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data) || !e.Data.StartsWith("out_time="))
                        return;
                    TimeSpan position;
                    if (TimeSpan.TryParse(e.Data.Substring("out_time=".Length),
                        CultureInfo.InvariantCulture, out position))
                    {
                        scannedPackets = true;
                        packetSeconds = Math.Max(packetSeconds, position.TotalSeconds);
                    }
                };
                process.ErrorDataReceived += (sender, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    var match = DurationPattern.Match(e.Data);
                    if (match.Success)
                    {
                        TimeSpan duration;
                        if (TimeSpan.TryParse(match.Groups[1].Value,
                            CultureInfo.InvariantCulture, out duration))
                            headerSeconds = duration.TotalSeconds;
                    }
                    lock (diagnostics) AppendDiagnostic(diagnostics, e.Data);
                };
                await RunProcessAndWaitAsync(process, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                {
                    string details;
                    lock (diagnostics) details = diagnostics.ToString().Trim();
                    throw new InvalidOperationException("저장된 MP4를 FFmpeg가 끝까지 읽지 못했습니다." +
                        (details.Length == 0 ? "" : Environment.NewLine + details));
                }
            }

            double expected = target.TotalSeconds;
            // Allow small keyframe/timestamp differences inherent to -c copy.
            double tolerance = Math.Min(3.0, Math.Max(0.5, expected * 0.01));
            tolerance = Math.Min(tolerance, expected * 0.25);
            double minimum = expected - tolerance;
            if (!headerSeconds.HasValue || headerSeconds.Value < minimum)
                throw new InvalidOperationException(
                    "영상 길이 검증 실패. 요청 구간 " + expected.ToString("0.0", CultureInfo.InvariantCulture) +
                    "초, 실제 파일 " + (headerSeconds.HasValue ?
                        headerSeconds.Value.ToString("0.0", CultureInfo.InvariantCulture) : "알 수 없음") +
                    "초. 다운로드가 중간에 종료되었을 수 있습니다.");
            if (!scannedPackets || packetSeconds < minimum)
                throw new InvalidOperationException(
                    "영상 패킷 검증 실패. 요청 구간 " +
                    expected.ToString("0.0", CultureInfo.InvariantCulture) +
                    "초, 실제 패킷 " + packetSeconds.ToString("0.0", CultureInfo.InvariantCulture) +
                    "초. 다운로드가 정상적으로 완료되지 않았습니다.");
        }

        private static ProcessStartInfo MakeStartInfo(string args)
        {
            return new ProcessStartInfo
            {
                FileName = FfmpegPath,
                Arguments = args,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
        }

        private static void AppendDiagnostic(StringBuilder builder, string value)
        {
            builder.AppendLine(value);
            if (builder.Length > 8000)
                builder.Remove(0, builder.Length - 8000);
        }

        private static async Task RunProcessAndWaitAsync(Process process, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!process.Start())
                throw new InvalidOperationException("FFmpeg를 실행할 수 없습니다.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using (token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            }))
            {
                await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
        }
    }
}
