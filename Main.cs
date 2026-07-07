global using static RealisticHUD.Main.Logger;
using System.IO;
using System.Reflection;
using ModLoader.Framework;
using ModLoader.Framework.Attributes;

namespace RealisticHUD.Main;

[ItemId("tomonary.realistichud")] // Harmony ID for your mod, make sure this is unique
public class Main : VtolMod
{
    public string ModFolder;

    private RealisticHudInjector injector;

    private void Awake()
    {
        ModFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        Log($"Awake at {ModFolder}");
        injector = gameObject.AddComponent<RealisticHudInjector>();
    }

    public override void UnLoad()
    {
        if (injector != null)
        {
            injector.Cleanup();
            Destroy(injector);
            injector = null;
        }
        Log("Mod unloaded.");
    }
}