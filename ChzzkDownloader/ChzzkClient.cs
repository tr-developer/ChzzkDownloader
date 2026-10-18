using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ChzzkDownloader
{
    internal sealed class VodQuality
    {
        internal string Label { get; set; }
        internal string Url { get; set; }
        internal int Height { get; set; }
        internal long Bandwidth { get; set; }
        internal bool IsHls { get; set; }
        public override string ToString() { return Label; }
    }

    internal sealed class VodInfo
    {
        internal string VideoNo { get; set; }
        internal string Title { get; set; }
        internal string ChannelName { get; set; }
        internal TimeSpan Duration { get; set; }
        internal List<VodQuality> Qualities { get; set; }
    }

    // Unofficial CHZZK APIs may change without notice.
    internal sealed class ChzzkClient : IDisposable
    {
        private static readonly Regex VideoPattern = new Regex(
            @"^https?://chzzk\.naver\.com/video/(\d+)(?:[/?#].*)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Mp4Pattern = new Regex(
            @"https?://[^\s""'\\<>]+?\.mp4(?:\?[^\s""'\\<>]*)?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private readonly HttpClient _http;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer
        {
            MaxJsonLength = int.MaxValue, RecursionLimit = 128
        };

        internal ChzzkClient()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
            _http.DefaultRequestHeaders.Referrer = new Uri("https://chzzk.naver.com/");
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ko-KR,ko;q=0.9");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Front-Client-Platform-Type", "PC");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Front-Client-Product-Type", "web");
        }

        internal static bool TryExtractVideoNo(string value, out string videoNo)
        {
            videoNo = null;
            string text = (value ?? "").Trim();
            if (Regex.IsMatch(text, @"^\d{1,20}$")) { videoNo = text; return true; }
            var match = VideoPattern.Match(text);
            if (!match.Success) return false;
            videoNo = match.Groups[1].Value;
            return true;
        }

        internal async Task<VodInfo> GetVodAsync(string videoNo, CancellationToken cancellationToken)
        {
            IDictionary<string, object> metadata = null;
            Exception lastError = null;
            // User-facing numeric number is videoNo; internal playback videoId can be different.
            foreach (string version in new[] { "v2", "v3" })
            {
                try
                {
                    var envelope = await GetJsonAsync(
                        "https://api.chzzk.naver.com/service/" + version + "/videos/" + videoNo,
                        cancellationToken).ConfigureAwait(false);
                    int code = (int)GetNumber(envelope, "code");
                    if (code != 0 && code != 200)
                        throw new InvalidOperationException("치지직 API 응답 오류: " + GetString(envelope, "message"));
                    metadata = AsObject(Get(envelope, "content"));
                    if (metadata == null) throw new InvalidOperationException("영상 정보가 비어 있습니다.");
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is HttpRequestException ||
                                           ex is InvalidOperationException)
                {
                    lastError = ex;
                }
            }

            if (metadata == null)
                throw new InvalidOperationException(
                    "영상 정보를 읽지 못했습니다. 영상이 삭제되었거나 로그인/인증이 필요할 수 있습니다.", lastError);

            var info = new VodInfo
            {
                VideoNo = videoNo,
                Title = GetString(metadata, "videoTitle") ?? ("chzzk_" + videoNo),
                ChannelName = GetString(AsObject(Get(metadata, "channel")), "channelName") ?? "알 수 없음",
                Duration = TimeSpan.FromSeconds(Math.Max(0, GetNumber(metadata, "duration"))),
                Qualities = new List<VodQuality>()
            };

            // 1. Prefer a direct MP4 URL inside /service/v2/videos/{videoNo}.
            // Handles regular object values, arrays and embedded JSON strings.
            FindDirectMp4(metadata, info.Qualities, 0, 0, 0);

            // 2. Some videos expose only inKey/videoId; then query the playback manifest.
            Exception playbackError = null;
            if (info.Qualities.Count == 0)
            {
                string videoId = GetString(metadata, "videoId");
                string inKey = GetString(metadata, "inKey");
                if (!string.IsNullOrWhiteSpace(videoId) && !string.IsNullOrWhiteSpace(inKey))
                {
                    try
                    {
                        var manifest = await GetJsonAsync(
                            "https://apis.naver.com/neonplayer/vodplay/v2/playback/" +
                            Uri.EscapeDataString(videoId) + "?key=" + Uri.EscapeDataString(inKey),
                            cancellationToken).ConfigureAwait(false);
                        FindManifestMp4(manifest, info.Qualities);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is HttpRequestException ||
                                               ex is InvalidOperationException)
                    {
                        playbackError = ex;
                    }
                }
            }

            // 3. Live-rewind replays may expose an HLS URL instead of a progressive MP4.
            if (info.Qualities.Count == 0)
                TryAddRewindHls(GetString(metadata, "liveRewindPlaybackJson"), info.Qualities);

            if (info.Qualities.Count == 0)
                throw new InvalidOperationException(
                    "MP4/HLS 다운로드 주소를 찾지 못했습니다. 비공개·인증 영상이거나 치지직 API 응답 형식이 변경되었을 수 있습니다.",
                    playbackError);

            info.Qualities = info.Qualities
                .GroupBy(q => q.Url, StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderBy(q => q.IsHls)
                .ThenByDescending(q => q.Height)
                .ThenByDescending(q => q.Bandwidth)
                .ToList();
            return info;
        }

        private async Task<IDictionary<string, object>> GetJsonAsync(string url, CancellationToken token)
        {
            using (HttpResponseMessage response = await _http.GetAsync(url, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var root = AsObject(_json.DeserializeObject(body));
                if (root == null) throw new InvalidOperationException("서버 JSON 응답이 올바르지 않습니다.");
                return root;
            }
        }

        private static void FindDirectMp4(object node, List<VodQuality> result,
            int depth, int inheritedHeight, long inheritedBandwidth)
        {
            if (depth > 30 || node == null) return;
            var dict = AsObject(node);
            if (dict != null)
            {
                int height = (int)GetNumber(dict, "height");
                if (height == 0) height = (int)GetNumber(dict, "videoHeight");
                if (height == 0) height = inheritedHeight;
                long bandwidth = (long)GetNumber(dict, "bandwidth");
                if (bandwidth == 0) bandwidth = inheritedBandwidth;
                foreach (var entry in dict)
                    FindDirectMp4(entry.Value, result, depth + 1, height, bandwidth);
                return;
            }
            var array = node as IList;
            if (array != null)
            {
                foreach (object item in array)
                    FindDirectMp4(item, result, depth + 1, inheritedHeight, inheritedBandwidth);
                return;
            }
            var raw = node as string;
            if (raw == null) return;
            string normalized = raw.Replace(@"\/", "/").Replace(@"\u0026", "&")
                .Replace("&amp;", "&");
            foreach (Match match in Mp4Pattern.Matches(normalized))
            {
                string url = match.Value;
                Uri uri;
                if (!Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                    uri.Scheme != Uri.UriSchemeHttps ||
                    !uri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) continue;
                if (result.Any(x => string.Equals(x.Url, url, StringComparison.Ordinal))) continue;
                result.Add(new VodQuality
                {
                    Label = (inheritedHeight > 0 ? inheritedHeight + "p" : "원본") + " · MP4",
                    Height = inheritedHeight,
                    Bandwidth = inheritedBandwidth,
                    Url = url,
                    IsHls = false
                });
            }
        }

        private static void FindManifestMp4(IDictionary<string, object> root, List<VodQuality> result)
        {
            // Most API versions return period[].adaptationSet[].representation[].
            object periods = Get(root, "period");
            var mpd = AsObject(Get(root, "MPD"));
            if (periods == null && mpd != null) periods = Get(mpd, "Period");
            foreach (object p in AsArray(periods))
            {
                var period = AsObject(p);
                foreach (object a in AsArray(Get(period, "adaptationSet") ?? Get(period, "AdaptationSet")))
                {
                    var adaptation = AsObject(a);
                    string mime = GetString(adaptation, "mimeType");
                    foreach (object r in AsArray(Get(adaptation, "representation") ??
                                                     Get(adaptation, "Representation")))
                    {
                        var rep = AsObject(r);
                        string type = GetString(rep, "mimeType") ?? mime;
                        if (!string.Equals(type, "video/mp4", StringComparison.OrdinalIgnoreCase)) continue;
                        FindDirectMp4(rep, result, 0, (int)GetNumber(rep, "height"),
                            (long)GetNumber(rep, "bandwidth"));
                    }
                }
            }

            // For future API variations, still inspect nested values for an MP4.
            if (result.Count == 0) FindDirectMp4(root, result, 0, 0, 0);
        }

        private void TryAddRewindHls(string raw, List<VodQuality> result)
        {
            if (string.IsNullOrEmpty(raw)) return;
            try
            {
                var parsed = AsObject(_json.DeserializeObject(raw));
                foreach (object value in AsArray(Get(parsed, "media")))
                {
                    var media = AsObject(value);
                    if (!string.Equals(GetString(media, "protocol"), "HLS",
                        StringComparison.OrdinalIgnoreCase)) continue;
                    string url = GetString(media, "path");
                    Uri uri;
                    if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps)
                        continue;
                    result.Add(new VodQuality { Label = "HLS · 자동 화질", Url = url,
                        IsHls = true, Height = 0 });
                    return;
                }
            }
            catch (ArgumentException) { /* Optional malformed embedded JSON. */ }
        }

        private static IDictionary<string, object> AsObject(object value)
        {
            return value as IDictionary<string, object>;
        }

        private static IList AsArray(object value)
        {
            return (value as IList) ?? new object[0];
        }

        private static object Get(IDictionary<string, object> value, string key)
        {
            object item;
            return value != null && value.TryGetValue(key, out item) ? item : null;
        }

        private static string GetString(IDictionary<string, object> value, string key)
        {
            var item = Get(value, key);
            return item == null ? null : Convert.ToString(item, CultureInfo.InvariantCulture);
        }

        private static double GetNumber(IDictionary<string, object> value, string key)
        {
            double number;
            var item = Get(value, key);
            return item != null && double.TryParse(Convert.ToString(item, CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : 0;
        }

        public void Dispose() { _http.Dispose(); }
    }
}
