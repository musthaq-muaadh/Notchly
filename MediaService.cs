using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Media.Control;

namespace Notchly
{
    /// <summary>Reads/controls whatever is playing (Spotify, browser, etc.) via Windows' media session API.</summary>
    public sealed class MediaService
    {
        GlobalSystemMediaTransportControlsSessionManager? _mgr;
        GlobalSystemMediaTransportControlsSession? _session;
        DateTimeOffset _timelineStamp = DateTimeOffset.Now;

        public event Action? Changed;

        public string Title { get; private set; } = "";
        public string Artist { get; private set; } = "";
        public bool IsPlaying { get; private set; }
        public BitmapImage? Art { get; private set; }
        public TimeSpan Position { get; private set; }
        public TimeSpan Duration { get; private set; }
        public bool HasSession => _session != null && Title.Length > 0;

        public TimeSpan EstimatedPosition
        {
            get
            {
                var p = IsPlaying ? Position + (DateTimeOffset.Now - _timelineStamp) : Position;
                if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
                return p < TimeSpan.Zero ? TimeSpan.Zero : p;
            }
        }

        public async Task InitAsync()
        {
            try
            {
                _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _mgr.CurrentSessionChanged += (_, _) => Attach();
                Attach();
            }
            catch { /* media API unavailable */ }
        }

        void Attach()
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnChanged;
                _session.PlaybackInfoChanged -= OnChanged;
                _session.TimelinePropertiesChanged -= OnChanged;
            }
            _session = _mgr?.GetCurrentSession();
            if (_session != null)
            {
                _session.MediaPropertiesChanged += OnChanged;
                _session.PlaybackInfoChanged += OnChanged;
                _session.TimelinePropertiesChanged += OnChanged;
            }
            _ = RefreshAsync();
        }

        void OnChanged(GlobalSystemMediaTransportControlsSession s, object a) => _ = RefreshAsync();

        async Task RefreshAsync()
        {
            try
            {
                var s = _session;
                if (s == null)
                {
                    Title = Artist = "";
                    IsPlaying = false;
                    Art = null;
                    Changed?.Invoke();
                    return;
                }

                var props = await s.TryGetMediaPropertiesAsync();
                Title = props.Title ?? "";
                Artist = props.Artist ?? "";

                var pb = s.GetPlaybackInfo();
                IsPlaying = pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                var tl = s.GetTimelineProperties();
                Position = tl.Position;
                Duration = tl.EndTime - tl.StartTime;
                _timelineStamp = tl.LastUpdatedTime;

                BitmapImage? art = null;
                if (props.Thumbnail != null)
                {
                    using var rs = await props.Thumbnail.OpenReadAsync();
                    using var stream = rs.AsStreamForRead();
                    var ms = new MemoryStream();
                    await stream.CopyToAsync(ms);
                    ms.Position = 0;
                    art = new BitmapImage();
                    art.BeginInit();
                    art.CacheOption = BitmapCacheOption.OnLoad;
                    art.StreamSource = ms;
                    art.EndInit();
                    art.Freeze();
                }
                Art = art;
            }
            catch { }
            Changed?.Invoke();
        }

        public async Task PlayPauseAsync() { if (_session != null) await _session.TryTogglePlayPauseAsync(); }
        public async Task NextAsync() { if (_session != null) await _session.TrySkipNextAsync(); }
        public async Task PreviousAsync() { if (_session != null) await _session.TrySkipPreviousAsync(); }
    }
}
