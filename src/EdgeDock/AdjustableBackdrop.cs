using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace EdgeDock;

/// <summary>
/// Uses the Windows backdrop controllers so the material recipe can be tuned
/// without changing the opacity of the window or its content.
/// </summary>
internal sealed class AdjustableBackdrop(BackdropMaterial material, double backgroundVisibility) : SystemBackdrop
{
    // EdgeDock has a fixed dark theme. Customizing a controller appearance property opts it
    // out of its automatic Light/Dark recipe, so keep its tint and solid fallback
    // in the same dark family before applying the user adjustment.
    private static readonly Windows.UI.Color DarkBackdropColor = Windows.UI.Color.FromArgb(255, 17, 16, 22);
    private readonly float _backgroundVisibility = (float)Math.Clamp(backgroundVisibility / 100, 0, 1);
    private ICompositionSupportsSystemBackdrop? _target;
    private XamlRoot? _xamlRoot;
    private MicaController? _micaController;
    private DesktopAcrylicController? _acrylicController;

    internal static bool IsSupported(BackdropMaterial requestedMaterial) => requestedMaterial == BackdropMaterial.Acrylic
        ? DesktopAcrylicController.IsSupported()
        : MicaController.IsSupported();

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _target = connectedTarget;
        _xamlRoot = xamlRoot;
        ConnectController();
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        if (!ReferenceEquals(disconnectedTarget, _target)) return;
        DisconnectController();
        _target = null;
        _xamlRoot = null;
        base.OnTargetDisconnected(disconnectedTarget);
    }

    private void ConnectController()
    {
        if (_target is null || _xamlRoot is null) return;

        var configuration = GetDefaultSystemBackdropConfiguration(_target, _xamlRoot);
        if (material == BackdropMaterial.Acrylic)
        {
            if (!DesktopAcrylicController.IsSupported()) return;
            _acrylicController = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base };
            _acrylicController.AddSystemBackdropTarget(_target);
            _acrylicController.SetSystemBackdropConfiguration(configuration);
            ApplyVisibility(_acrylicController);
            return;
        }

        if (!MicaController.IsSupported()) return;
        _micaController = new MicaController { Kind = MicaKind.Base };
        _micaController.AddSystemBackdropTarget(_target);
        _micaController.SetSystemBackdropConfiguration(configuration);
        ApplyVisibility(_micaController);
    }

    private void ApplyVisibility(MicaController controller)
    {
        ApplyDarkRecipe(controller);
        controller.TintOpacity = AdjustOpacity(controller.TintOpacity);
        controller.LuminosityOpacity = AdjustOpacity(controller.LuminosityOpacity);
    }

    private void ApplyVisibility(DesktopAcrylicController controller)
    {
        ApplyDarkRecipe(controller);
        controller.TintOpacity = AdjustOpacity(controller.TintOpacity);
        controller.LuminosityOpacity = AdjustOpacity(controller.LuminosityOpacity);
    }

    private static void ApplyDarkRecipe(MicaController controller)
    {
        controller.TintColor = DarkBackdropColor;
        controller.FallbackColor = DarkBackdropColor;
    }

    private static void ApplyDarkRecipe(DesktopAcrylicController controller)
    {
        controller.TintColor = DarkBackdropColor;
        controller.FallbackColor = DarkBackdropColor;
    }

    private float AdjustOpacity(float windowsDefault)
    {
        // 50 preserves the Windows recipe. Lower values add tint; higher values
        // reveal more of the wallpaper (Mica) or window behind (Acrylic).
        return _backgroundVisibility <= 0.5f
            ? Lerp(1, windowsDefault, _backgroundVisibility * 2)
            : Lerp(windowsDefault, 0, (_backgroundVisibility - 0.5f) * 2);
    }

    private static float Lerp(float start, float end, float amount) => start + ((end - start) * amount);

    private void DisconnectController()
    {
        if (_target is not null)
        {
            _micaController?.RemoveSystemBackdropTarget(_target);
            _acrylicController?.RemoveSystemBackdropTarget(_target);
        }

        _micaController?.Dispose();
        _micaController = null;
        _acrylicController?.Dispose();
        _acrylicController = null;
    }
}
