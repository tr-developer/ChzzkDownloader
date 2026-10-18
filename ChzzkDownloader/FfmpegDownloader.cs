using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChzzkDownloader
{
    internal static class FfmpegDownloader
    {
        // No shell is involved; each user-provided URL/path is quoted as a single argument.
        private static string Quote(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }

        internal static async Task DownloadAsync(string ffmpegPath, string url,
            TimeSpan start, TimeSpan end, string output, IProgress<double> progress,
            Action<string> log, CancellationToken cancellationToken)
        {
            string directory = Path.GetDirectoryName(output);
            Directory.CreateDirectory(directory);
            string partial = Path.Combine(directory,
                Path.GetFileNameWithoutExtension(output) + ".partial.mp4");
            if (File.Exists(partial))
                throw new IOException("임시 파일이 존재합니다. 파일 확인 후 삭제해 주세요: " + partial);

            // Keep the requested input-seeking layout: -ss START -to END -i URL -c copy OUTPUT.
            string args = "-hide_banner -nostdin -y -loglevel warning " +
                          "-stats_period 0.5 -progress pipe:1 " +
                          "-ss " + TimeCode.Format(start) + " " +
                          "-to " + TimeCode.Format(end) + " " +
                          "-i " + Quote(url) + " -c copy -movflags +faststart " + Quote(partial);
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath, Arguments = args,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };

            var stderr = new StringBuilder();
            bool completed = false;
            try
            {
                using (var process = new Process { StartInfo = psi })
                {
                    TimeSpan duration = end - start;
                    process.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data == null || !e.Data.StartsWith("out_time=")) return;
                        TimeSpan processed;
                        if (TimeSpan.TryParse(e.Data.Substring("out_time=".Length),
                            CultureInfo.InvariantCulture, out processed) && duration.TotalSeconds > 0)
                        {
                            progress.Report(Math.Max(0, Math.Min(100,
                                processed.TotalSeconds * 100.0 / duration.TotalSeconds)));
                        }
                    };
                    process.ErrorDataReceived += (s, e) =>
                    {
                        if (string.IsNullOrEmpty(e.Data)) return;
                        lock (stderr)
                        {
                            stderr.AppendLine(e.Data);
                            if (stderr.Length > 3500) stderr.Remove(0, stderr.Length - 3500);
                        }
                    };

                    cancellationToken.ThrowIfCancellationRequested();
                    if (!process.Start()) throw new InvalidOperationException("FFmpeg 시작 실패");
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    log("FFmpeg 실행: " + TimeCode.Format(start) + " - " + TimeCode.Format(end));

                    using (cancellationToken.Register(() =>
                    {
                        try { if (!process.HasExited) process.Kill(); }
                        catch (InvalidOperationException) { }
                        catch (Win32Exception) { }
                    }))
                    {
                        await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0)
                    {
                        string detail;
                        lock (stderr) detail = stderr.ToString().Trim();
                        throw new InvalidOperationException("FFmpeg 종료 코드 " + process.ExitCode +
                            (detail.Length > 0 ? Environment.NewLine + detail : ""));
                    }
                }

                if (!File.Exists(partial) || new FileInfo(partial).Length == 0)
                    throw new IOException("생성된 동영상이 비어 있습니다.");
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(output)) File.Delete(output);
                File.Move(partial, output);
                completed = true;
                progress.Report(100);
                log("저장 완료: " + output);
            }
            finally
            {
                if (!completed && File.Exists(partial))
                {
                    try { File.Delete(partial); }
                    catch (IOException) { log("임시 파일 삭제 실패: " + partial); }
                    catch (UnauthorizedAccessException) { log("임시 파일 삭제 권한 없음: " + partial); }
                }
            }
        }
    }
}
