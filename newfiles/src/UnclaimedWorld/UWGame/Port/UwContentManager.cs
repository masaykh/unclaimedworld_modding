using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;

namespace UWGame.Port;

/// <summary>
/// PORT DEVIATION 14 (see PORTING-NOTES.md).
///
/// A ContentManager that repairs a model's vertex layout as it is loaded - see
/// <see cref="SkinnedVertexCompat"/> for what is wrong with it and why the data rather than the
/// shader is the right place to fix it.
///
/// This exists as a ContentManager subclass rather than a call at each load site because models
/// are loaded from more than fifty places across GameData and the client, and a fix that has to
/// be remembered at every one of them is a fix that will be missed. Loading is the one choke
/// point every model passes through.
///
/// The rewrite is DesktopGL-only. WindowsDX binds the shipped Color-format blend weights as
/// R8G8B8A8_UNORM and normalizes them in hardware, so there is nothing to repair there - and
/// rewriting every skinned model's vertex buffers on the target that already works trades real
/// memory and a change to what skinFX is fed for no benefit at all. The subclass itself stays on
/// both targets so the two builds keep loading content through the same type.
/// </summary>
public sealed class UwContentManager : ContentManager
{
    public UwContentManager(IServiceProvider services)
        : base(services)
    {
    }

    public UwContentManager(IServiceProvider services, string rootDirectory)
        : base(services, rootDirectory)
    {
    }

    /// <summary>
    /// Folder searched before <c>Content\</c>, relative to the game root. Any <c>.xnb</c> here
    /// wins over the shipped one with the same name.
    ///
    /// PORT DEVIATION 18 (see PORTING-NOTES.md). This is how the port avoids modifying the
    /// game's own assets. The 19 shipped effects are MGFX v8 containers and MonoGame 3.8
    /// requires v10, so the port used to rewrite them IN PLACE in <c>Content\</c> - with a
    /// backup, but still editing files the studio shipped, and still leaving the game broken
    /// until someone remembered to run the installer. Anyone who unzipped the binaries and ran
    /// the exe directly got MonoGame's bare "This MGFX effect is for an older release of
    /// MonoGame" crash several screens into startup.
    ///
    /// Shipping the converted effects here instead means <c>Content\</c> is never touched, there
    /// is no conversion step to forget, and removing the port is deleting files rather than
    /// restoring a backup. It also gives modders an override folder for free: drop any
    /// replacement .xnb in here and the game prefers it.
    /// </summary>
    public const string OverrideFolderName = "port-content";

    /// <summary>
    /// Prefers <c>port-content\&lt;asset&gt;.xnb</c> over <c>Content\&lt;asset&gt;.xnb</c>.
    ///
    /// Overriding OpenStream rather than Load is deliberate: it is the single funnel every
    /// content type passes through, so this covers effects, textures, models and everything
    /// else without enumerating types. Anything absent from the override folder falls through to
    /// the base implementation untouched, which keeps TitleContainer's behaviour for normal
    /// assets.
    /// </summary>
    /// <summary>
    /// Explicit override folder. When null the sibling of <see cref="ContentManager.RootDirectory"/>
    /// is used, which is what the game relies on.
    ///
    /// Settable so a verification tool can point at a packaged override folder without having to
    /// sit next to it - tools/ContentProbe uses it to probe the PRISTINE game content against a
    /// freshly built port-content\, which is the combination that actually ships and the one
    /// that cannot be tested by probing a content folder that was converted in place.
    /// </summary>
    public string OverrideFolder { get; set; }

    protected override Stream OpenStream(string assetName)
    {
        try
        {
            string folder = OverrideFolder ?? OverrideFolderFor(RootDirectory);
            if (folder != null)
            {
                string overridePath = Path.Combine(folder, assetName) + ".xnb";
                if (File.Exists(overridePath))
                {
                    return File.OpenRead(overridePath);
                }
            }
        }
        catch (Exception)
        {
            // An unreadable override must not stop the shipped asset from loading.
        }

        return base.OpenStream(assetName);
    }

    /// <summary>
    /// The override folder that pairs with a given content root: its SIBLING, not a path
    /// relative to the working directory.
    ///
    /// Resolved this way so it works for the game and for tools alike. The game runs with the
    /// working directory at the game root and <c>RootDirectory = "Content"</c>, giving
    /// <c>&lt;game&gt;\port-content</c>. A tool pointed at an absolute
    /// <c>D:\somewhere\Content</c> gets <c>D:\somewhere\port-content</c>, which is what lets
    /// tools/ContentProbe validate a packaged installation it is not running inside.
    /// </summary>
    public static string OverrideFolderFor(string contentRoot)
    {
        if (string.IsNullOrEmpty(contentRoot))
        {
            return null;
        }
        string parent = Path.GetDirectoryName(Path.GetFullPath(contentRoot));
        return parent == null ? null : Path.Combine(parent, OverrideFolderName);
    }

    public override T Load<T>(string assetName)
    {
        T asset = base.Load<T>(assetName);

#if UW_GL
        if (asset is Model model)
        {
            GraphicsDevice device =
                (ServiceProvider.GetService(typeof(IGraphicsDeviceService)) as IGraphicsDeviceService)
                ?.GraphicsDevice;
            if (device != null) SkinnedVertexCompat.Normalize(model, device);
        }
#endif

        return asset;
    }
}
