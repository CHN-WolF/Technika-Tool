using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TechnikaTool.App.Services;

namespace TechnikaTool.App.Views;

public partial class MediaPlaybackView : UserControl, IDisposable
{
    public static readonly DependencyProperty SourcePathProperty =
        DependencyProperty.Register(
            nameof(SourcePath),
            typeof(string),
            typeof(MediaPlaybackView),
            new PropertyMetadata(null, OnSourcePathChanged));

    private readonly DispatcherTimer clock = new()
    {
        Interval = TimeSpan.FromMilliseconds(200)
    };

    private EmbeddedMediaSession? session;
    private WriteableBitmap? bitmap;
    private bool seeking;
    private bool disposed;
    private int loadGeneration;
    private bool playing;

    public MediaPlaybackView()
    {
        InitializeComponent();
        clock.Tick += OnClockTick;
    }

    public string? SourcePath
    {
        get => (string?)GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    public void StopAndRelease()
    {
        clock.Stop();
        EmbeddedMediaSession? current = session;
        session = null;
        current?.Dispose();
        playing = false;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        StopAndRelease();
        clock.Tick -= OnClockTick;
    }

    private static void OnSourcePathChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        if (sender is MediaPlaybackView view
            && view.IsLoaded
            && view.IsVisible)
        {
            view.LoadSource(args.NewValue as string);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (IsVisible) LoadSource(SourcePath);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => StopAndRelease();

    private void OnIsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (IsVisible) LoadSource(SourcePath);
        else StopAndRelease();
    }

    private void LoadSource(string? path)
    {
        int generation = ++loadGeneration;
        StopAndRelease();
        bitmap = null;
        VideoImage.Source = null;
        PositionSlider.Value = 0;
        PositionSlider.Maximum = 1;
        ElapsedText.Text = "00:00";
        DurationText.Text = "00:00";
        PlayPauseButton.Content = "Play";
        MediaNameText.Text = string.Empty;
        AudioPlaceholder.Visibility = Visibility.Visible;
        StageTitleText.Text = "Audio preview";
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            PlaybackStatusText.Text = "No playable media is selected.";
            return;
        }

        MediaNameText.Text = Path.GetFileName(path);
        bool audio = IsAudio(path);
        StageTitleText.Text = audio ? "Audio preview" : "Decoding video…";
        PlaybackStatusText.Text = "Opening the built-in decoder…";
        try
        {
            EmbeddedMediaSession opened = EmbeddedMediaSession.OpenFile(path);
            if (generation != loadGeneration)
            {
                opened.Dispose();
                return;
            }

            session = opened;
            session.Loop = LoopCheckBox.IsChecked == true;
            session.Volume = (int)VolumeSlider.Value;
            session.FrameReady += (pixels, width, height, stride) =>
                OnFrameReady(generation, pixels, width, height, stride);
            session.PlaybackEnded += () =>
                Dispatcher.BeginInvoke(() =>
                {
                    if (generation != loadGeneration) return;
                    playing = false;
                    PlayPauseButton.Content = "Play";
                    PlaybackStatusText.Text = "Finished";
                });
            session.Failed += message =>
                Dispatcher.BeginInvoke(() =>
                {
                    if (generation != loadGeneration) return;
                    playing = false;
                    PlayPauseButton.Content = "Play";
                    PlaybackStatusText.Text = message;
                    StageTitleText.Text = "Decoder error";
                });
            DurationText.Text = FormatTime((long)opened.Duration.TotalMilliseconds);
            if (opened.Duration > TimeSpan.Zero)
            {
                PositionSlider.Maximum = opened.Duration.TotalMilliseconds;
            }

            if (!audio && opened.HasVideo)
            {
                StageTitleText.Text = "Decoding video…";
            }
            else if (!opened.HasAudio && opened.HasVideo)
            {
                StageTitleText.Text = "Decoding video…";
            }

            playing = true;
            PlayPauseButton.Content = "Pause";
            PlaybackStatusText.Text = opened.HasVideo
                ? "Playing video"
                : "Playing audio";
            clock.Start();
            session.Play();
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or IOException
                or DllNotFoundException
                or TypeInitializationException
                or InvalidOperationException)
        {
            PlaybackStatusText.Text = exception.Message;
            StageTitleText.Text = "Decoder error";
        }
    }

    private void OnFrameReady(
        int generation,
        byte[] pixels,
        int width,
        int height,
        int stride)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (disposed || generation != loadGeneration) return;
            if (bitmap is null
                || bitmap.PixelWidth != width
                || bitmap.PixelHeight != height)
            {
                bitmap = new WriteableBitmap(
                    width,
                    height,
                    96,
                    96,
                    System.Windows.Media.PixelFormats.Bgra32,
                    null);
                VideoImage.Source = bitmap;
            }

            try
            {
                bitmap.WritePixels(
                    new Int32Rect(0, 0, width, height),
                    pixels,
                    stride,
                    0);
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException)
            {
                PlaybackStatusText.Text =
                    "The video frame could not be displayed. " + exception.Message;
                return;
            }

            AudioPlaceholder.Visibility = Visibility.Collapsed;
        });
    }

    private void OnClockTick(object? sender, EventArgs e)
    {
        if (session is null || seeking) return;
        TimeSpan position = session.Position;
        if (!seeking)
        {
            PositionSlider.Value = Math.Min(
                PositionSlider.Maximum,
                Math.Max(0, position.TotalMilliseconds));
        }

        ElapsedText.Text = FormatTime((long)position.TotalMilliseconds);
        if (session.Duration > TimeSpan.Zero)
        {
            DurationText.Text = FormatTime((long)session.Duration.TotalMilliseconds);
            PositionSlider.Maximum = session.Duration.TotalMilliseconds;
        }
    }

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (session is null) return;
        if (playing)
        {
            session.Pause();
            playing = false;
            PlayPauseButton.Content = "Play";
            PlaybackStatusText.Text = "Paused";
        }
        else
        {
            session.Play();
            playing = true;
            PlayPauseButton.Content = "Pause";
            PlaybackStatusText.Text = session.HasVideo ? "Playing video" : "Playing audio";
        }
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        if (session is null) return;
        session.Stop();
        playing = false;
        PlayPauseButton.Content = "Play";
        PlaybackStatusText.Text = "Stopped";
        PositionSlider.Value = 0;
        ElapsedText.Text = "00:00";
    }

    private void OnSeekStarted(object sender, MouseButtonEventArgs e) =>
        seeking = true;

    private void OnSeekCompleted(object sender, MouseButtonEventArgs e)
    {
        session?.Seek(TimeSpan.FromMilliseconds(PositionSlider.Value));
        seeking = false;
    }

    private void OnVolumeChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (session is not null) session.Volume = (int)e.NewValue;
    }

    private void OnLoopChanged(object sender, RoutedEventArgs e)
    {
        if (session is not null) session.Loop = LoopCheckBox.IsChecked == true;
    }

    private static bool IsAudio(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatTime(long milliseconds)
    {
        TimeSpan value = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"mm\:ss");
    }
}
