using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ChzzkDownloader
{
    internal sealed class MainForm : Form
    {
        private static readonly Color Background = Color.FromArgb(19, 25, 35);
        private static readonly Color Surface = Color.FromArgb(34, 46, 61);
        private static readonly Color White = Color.FromArgb(241, 245, 249);
        private static readonly Color Muted = Color.FromArgb(168, 180, 196);
        private static readonly Color Green = Color.FromArgb(43, 212, 109);

        private readonly ChzzkClient _client = new ChzzkClient();
        private readonly TextBox _url, _start, _end, _folder, _fileName, _ffmpeg, _log;
        private readonly ComboBox _qualities;
        private readonly Label _videoInfo, _status;
        private readonly ProgressBar _progress;
        private readonly Button _lookup, _download, _cancel, _copyUrl;
        private CancellationTokenSource _cts;
        private VodInfo _vod;
        private bool _working;

        internal MainForm()
        {
            Text = "ChzzkDownloader - 치지직 다시보기 다운로더";
            ClientSize = new Size(880, 775);
            MinimumSize = new Size(760, 680);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Background;
            ForeColor = White;
            Font = new Font("Segoe UI", 10F);
            AutoScroll = true;

            LabelAt("ChzzkDownloader", 22, 16, 800, 34, 19, true, White);
            LabelAt("영상 주소를 입력한 뒤 화질과 다운로드 구간을 선택하세요.",
                22, 55, 800, 23, 9, false, Muted);
            LabelAt("다시보기 URL 또는 영상 번호", 22, 96, 450, 24, 10, true, White);
            _url = TextBoxAt("https://chzzk.naver.com/video/15707690", 22, 126, 694, 33, true);
            _lookup = ButtonAt("영상 정보 조회", 729, 126, 130, 33, true, true);
            _lookup.Click += async (s, e) => await LookupAsync();
            AcceptButton = _lookup;

            var infoCard = new Panel
            {
                Left = 22, Top = 178, Width = 837, Height = 62,
                BackColor = Surface, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(infoCard);
            _videoInfo = new Label
            {
                Text = "영상 정보를 조회하면 제목, 채널명과 전체 길이를 표시합니다.",
                Location = new Point(12, 10), Size = new Size(810, 48),
                AutoEllipsis = true, ForeColor = Muted,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            infoCard.Controls.Add(_videoInfo);

            LabelAt("화질 / 소스", 22, 260, 300, 22, 10, true, White);
            _qualities = new ComboBox
            {
                Left = 22, Top = 288, Width = 317,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Surface, ForeColor = White, FlatStyle = FlatStyle.Flat
            };
            Controls.Add(_qualities);
            _copyUrl = ButtonAt("직접 주소 복사", 351, 288, 143, 31, false, false);
            _copyUrl.Click += (s, e) =>
            {
                var item = _qualities.SelectedItem as VodQuality;
                if (item != null)
                {
                    Clipboard.SetText(item.Url);
                    Log("소스 주소를 복사했습니다. 만료 시 영상 정보를 다시 조회하세요.");
                }
            };
            LabelAt("기본값: MP4 최고 화질", 512, 291, 340, 22, 8.5F, false, Muted);

            LabelAt("시작 (HH:MM:SS)", 22, 342, 230, 23, 10, true, White);
            _start = TextBoxAt("00:00:00", 22, 369, 210, 34, false);
            LabelAt("종료 (HH:MM:SS)", 249, 342, 230, 23, 10, true, White);
            _end = TextBoxAt("00:00:00", 249, 369, 210, 34, false);
            LabelAt("조회 후 종료 시간이 영상 끝으로 설정됩니다.",
                477, 372, 370, 28, 8.5F, false, Muted);

            LabelAt("저장 위치", 22, 422, 330, 22, 10, true, White);
            string movies = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrWhiteSpace(movies))
                movies = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            _folder = TextBoxAt(movies, 22, 449, 694, 34, true);
            var browse = ButtonAt("폴더 선택", 729, 449, 130, 34, false, true);
            browse.Click += (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog { Description = "동영상을 저장할 폴더" })
                {
                    if (Directory.Exists(_folder.Text)) dlg.SelectedPath = _folder.Text;
                    if (dlg.ShowDialog(this) == DialogResult.OK) _folder.Text = dlg.SelectedPath;
                }
            };

            LabelAt("저장 파일명 (.mp4)", 22, 496, 400, 23, 10, true, White);
            _fileName = TextBoxAt("chzzk_video", 22, 523, 837, 34, true);
            LabelAt("FFmpeg 실행 파일 (ffmpeg.exe)", 22, 568, 400, 23, 10, true, White);
            var localFfmpeg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            _ffmpeg = TextBoxAt(File.Exists(localFfmpeg) ? localFfmpeg : "ffmpeg.exe",
                22, 595, 694, 34, true);
            var ffButton = ButtonAt("FFmpeg 선택", 729, 595, 130, 34, false, true);
            ffButton.Click += (s, e) =>
            {
                using (var dlg = new OpenFileDialog
                {
                    Title = "ffmpeg.exe 선택", Filter = "ffmpeg.exe|ffmpeg.exe|실행 파일|*.exe"
                })
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK) _ffmpeg.Text = dlg.FileName;
                }
            };

            _download = ButtonAt("다운로드 시작", 22, 650, 204, 41, true, false);
            _download.Click += async (s, e) => await DownloadAsync();
            _cancel = ButtonAt("취소", 241, 650, 105, 41, false, false);
            _cancel.Click += (s, e) =>
            {
                if (_cts == null) return;
                _cancel.Enabled = false;
                _status.Text = "취소 중...";
                _cts.Cancel();
            };

            _status = LabelAt("대기 중", 22, 703, 815, 23, 9, false, Muted);
            _progress = new ProgressBar
            {
                Left = 22, Top = 727, Width = 837, Height = 15,
                Minimum = 0, Maximum = 100,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
            };
            Controls.Add(_progress);
            _log = TextBoxAt("", 22, 753, 837, 82, true);
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.Font = new Font("Consolas", 9F);
            ClientSize = new Size(880, 850);
            MinimumSize = new Size(760, 760);
            _copyUrl.Enabled = false;
            _download.Enabled = false;
            _cancel.Enabled = false;
            Log("다시보기 URL 입력 → 영상 정보 조회 → 다운로드 시작");
        }

        private Label LabelAt(string value, int x, int y, int w, int h,
            float size, bool bold, Color color)
        {
            var label = new Label
            {
                Text = value, Left = x, Top = y, Width = w, Height = h,
                ForeColor = color, AutoEllipsis = true,
                Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular)
            };
            Controls.Add(label);
            return label;
        }

        private TextBox TextBoxAt(string value, int x, int y, int w, int h, bool resize)
        {
            var box = new TextBox
            {
                Text = value, Left = x, Top = y, Width = w, Height = h,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Surface, ForeColor = White, Font = new Font("Segoe UI", 10.5F),
                Anchor = AnchorStyles.Left | AnchorStyles.Top |
                         (resize ? AnchorStyles.Right : AnchorStyles.None)
            };
            Controls.Add(box);
            return box;
        }

        private Button ButtonAt(string value, int x, int y, int w, int h, bool accent, bool right)
        {
            var button = new Button
            {
                Text = value, Left = x, Top = y, Width = w, Height = h,
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                BackColor = accent ? Green : Surface,
                ForeColor = accent ? Color.FromArgb(14, 34, 21) : White,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Anchor = AnchorStyles.Top | (right ? AnchorStyles.Right : AnchorStyles.Left)
            };
            button.FlatAppearance.BorderSize = 0;
            Controls.Add(button);
            return button;
        }

        private void SetWorking(bool working)
        {
            _working = working;
            _lookup.Enabled = !working;
            _download.Enabled = !working && _vod != null;
            _copyUrl.Enabled = !working && _qualities.SelectedItem != null;
            _cancel.Enabled = working;
            _url.ReadOnly = working;
            _qualities.Enabled = !working;
            _start.ReadOnly = working;
            _end.ReadOnly = working;
        }

        private void EndOperation()
        {
            if (_cts != null) { _cts.Dispose(); _cts = null; }
            SetWorking(false);
        }

        private async Task LookupAsync()
        {
            string number;
            if (!ChzzkClient.TryExtractVideoNo(_url.Text, out number))
            {
                MessageBox.Show(this, "https://chzzk.naver.com/video/숫자 주소를 입력하세요.",
                    "잘못된 주소", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _vod = null;
            _qualities.Items.Clear();
            _videoInfo.Text = "영상 정보 조회 중...";
            _cts = new CancellationTokenSource();
            SetWorking(true);
            _status.Text = "치지직 API 접속 중...";
            _progress.Value = 0;
            try
            {
                VodInfo info = await _client.GetVodAsync(number, _cts.Token);
                _vod = info;
                _videoInfo.Text = info.Title + Environment.NewLine +
                                  info.ChannelName + " · 전체 " + TimeCode.Format(info.Duration);
                _videoInfo.ForeColor = White;
                foreach (var quality in info.Qualities) _qualities.Items.Add(quality);
                _qualities.SelectedIndex = 0;
                _start.Text = "00:00:00";
                _end.Text = TimeCode.Format(info.Duration);
                _fileName.Text = SafeName(info.Title, info.VideoNo);
                _status.Text = "정보 조회 완료 · 시간과 화질을 선택하세요.";
                Log("영상 정보 확인: " + info.Title);
            }
            catch (OperationCanceledException) { _status.Text = "조회 취소"; }
            catch (Exception ex)
            {
                _videoInfo.Text = "영상을 불러올 수 없습니다.";
                _status.Text = "조회 실패";
                Log("오류: " + ex.Message);
                MessageBox.Show(this, ex.Message, "영상 조회 오류",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { EndOperation(); }
        }

        private async Task DownloadAsync()
        {
            var selected = _qualities.SelectedItem as VodQuality;
            if (_vod == null || selected == null) return;

            string currentNo;
            if (!ChzzkClient.TryExtractVideoNo(_url.Text, out currentNo) ||
                currentNo != _vod.VideoNo)
            {
                MessageBox.Show(this, "주소가 변경되었습니다. 다시 영상 정보를 조회하세요.");
                return;
            }

            TimeSpan from, to;
            if (!TimeCode.TryParse(_start.Text, out from) ||
                !TimeCode.TryParse(_end.Text, out to) ||
                to <= from || _vod.Duration.TotalSeconds < 1 ||
                to > TimeSpan.FromSeconds(Math.Ceiling(_vod.Duration.TotalSeconds)))
            {
                MessageBox.Show(this,
                    "올바른 HH:MM:SS 시간을 입력하세요.\n종료 시간은 시작 시간보다 크고 영상 끝 이하여야 합니다.",
                    "시간 범위 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(_folder.Text) ||
                string.IsNullOrWhiteSpace(_ffmpeg.Text))
            {
                MessageBox.Show(this, "저장 폴더와 ffmpeg.exe 경로를 확인하세요.");
                return;
            }

            string path;
            try
            {
                string name = SafeName(_fileName.Text, _vod.VideoNo);
                path = Path.GetFullPath(Path.Combine(_folder.Text.Trim(), name + ".mp4"));
                if (File.Exists(path) &&
                    MessageBox.Show(this, "같은 이름의 파일이 존재합니다. 덮어쓸까요?",
                        "덮어쓰기 확인", MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question) != DialogResult.Yes) return;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException ||
                                       ex is PathTooLongException)
            {
                MessageBox.Show(this, "저장 경로가 잘못되었습니다: " + ex.Message);
                return;
            }

            _cts = new CancellationTokenSource();
            SetWorking(true);
            _progress.Value = 0;
            _status.Text = "최신 다운로드 주소 조회 중...";
            var report = new Progress<double>(p =>
            {
                if (IsDisposed) return;
                _progress.Value = Math.Max(0, Math.Min(100, (int)Math.Round(p)));
                _status.Text = "다운로드 중: " + p.ToString("0.0") + "%";
            });

            try
            {
                // Refresh the tokenized media URL just before invoking FFmpeg.
                VodInfo fresh = await _client.GetVodAsync(_vod.VideoNo, _cts.Token);
                VodQuality quality = fresh.Qualities.FirstOrDefault(q =>
                    q.IsHls == selected.IsHls && q.Height == selected.Height) ??
                    fresh.Qualities.FirstOrDefault(q => q.IsHls == selected.IsHls);
                if (quality == null)
                    throw new InvalidOperationException("선택한 형식의 다운로드 주소가 만료되었거나 사라졌습니다.");

                await FfmpegDownloader.DownloadAsync(_ffmpeg.Text.Trim(), quality.Url,
                    from, to, path, report, Log, _cts.Token);
                _status.Text = "다운로드 완료 · " + path;
                MessageBox.Show(this, "다운로드가 완료되었습니다.\n" + path,
                    "저장 완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (OperationCanceledException)
            {
                _status.Text = "사용자가 작업을 취소했습니다.";
                Log("다운로드가 취소되었습니다.");
            }
            catch (Exception ex)
            {
                _status.Text = "다운로드 실패";
                Log("오류: " + ex.Message);
                MessageBox.Show(this, ex.Message,
                    "다운로드 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { EndOperation(); }
        }

        private static string SafeName(string input, string fallback)
        {
            string name = (input ?? "").Trim();
            if (name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Trim().TrimEnd('.', ' ');
            if (name.Length > 90) name = name.Substring(0, 90).TrimEnd('.', ' ');
            return string.IsNullOrWhiteSpace(name) ? "chzzk_" + fallback : name;
        }

        private void Log(string message)
        {
            if (InvokeRequired)
            {
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke(new Action<string>(Log), message);
                return;
            }
            if (_log == null || IsDisposed) return;
            _log.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " +
                            message + Environment.NewLine);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_working)
            {
                e.Cancel = true;
                MessageBox.Show(this, "실행 중인 작업을 먼저 취소해 주세요.");
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_cts != null) _cts.Dispose();
                _client.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
