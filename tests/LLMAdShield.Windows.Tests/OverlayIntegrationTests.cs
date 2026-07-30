using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using LLMAdShield.Core;
using LLMAdShield.Windows.Platform.Capture;
using LLMAdShield.Windows.Platform.Overlay;

namespace LLMAdShield.Windows.Tests;

public sealed class OverlayIntegrationTests
{
    [Fact]
    public void ExternalTarget_CanShowAndHideCaptureExcludedOverlay()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var target = WindowCaptureCatalog.Enumerate().FirstOrDefault();
        Assert.NotNull(target);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var presenter = new WpfOverlayPresenter(target!, Dispatcher.CurrentDispatcher);
                presenter.ShowAsync(OverlayPresentation.AdvertisementMuted)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                Assert.True(presenter.IsVisible);
                presenter.HideAsync().AsTask().GetAwaiter().GetResult();
                presenter.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The overlay test thread did not finish.");

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
