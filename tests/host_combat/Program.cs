using System.Text.Json;
using Caverarria;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria.Graphics;

// Checks production proxy targeting/geometry, without entering a game or save.
int checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    checks++;
}
JsonElement Actor(int life, bool shootable, int left = 0, int right = 20)
    => JsonSerializer.SerializeToElement(new { x = 100, y = 80, left, right, top = 16, bottom = 8, life, shootable });

Check(CampaignRuntime.ScaleWeaponDamage(25, 1) == 3, "default host-to-native damage rounding preserved");
Check(CampaignRuntime.ScaleWeaponDamage(25, 2) == 5, "outgoing multiplier precedes native quantization");
Check(CampaignRuntime.ScaleWeaponDamage(25, 0) == 0, "zero outgoing multiplier cannot inflict minimum damage");
Check(CampaignRuntime.ScaleWeaponDamage(1, .1f) == 1, "positive damage retains native minimum");
Check(CampaignRuntime.ScaleWeaponDamage(int.MaxValue, 10) == 32767, "large modded damage cannot overflow guest ABI");
Check(CampaignRuntime.ScaleIncomingDamage(3, 1) == 30, "default incoming damage conversion preserved");
Check(CampaignRuntime.ScaleIncomingDamage(3, .5f) == 15, "incoming scaling happens in host HP before armor");
Check(CampaignRuntime.ScaleIncomingDamage(3, 0) == 0, "zero incoming multiplier suppresses damage");
Check(CampaignRuntime.ScaleIncomingDamage(3, 10) == 300, "incoming maximum scale applied");

var identity = new CaveEntity { NativeId = 17, NativeBoss = false, Epoch = 3, Generation = 12 };
Check(!identity.CheckActive(), "vanilla distance despawning can recycle a still-live guest proxy");
Check(identity.Owns(17, false, 3, 12), "current proxy identity rejected");
Check(!identity.Owns(18, false, 3, 12), "recycled host slot accepted a different guest actor");
Check(!identity.Owns(17, true, 3, 12), "boss/ordinary actor slot collision accepted");
Check(!identity.Owns(17, false, 4, 12), "proxy from an old scene epoch accepted");
Check(!identity.Owns(17, false, 3, 13), "guest slot reuse with a new generation accepted");
Check(CaveEntity.IsCombatTarget(Actor(0, true)), "zero-local-HP vulnerable boss part lost its host target");
Check(CaveEntity.IsCombatTarget(Actor(5, true)), "ordinary shootable enemy lost its host target");
Check(!CaveEntity.IsCombatTarget(Actor(1000, false)), "closed/invulnerable native phase became damageable");
var bounds = CaveEntity.CombatBounds(Actor(0, true));
Check(bounds.X == (int)CampaignRuntime.Origin.X + 80 * 2, "boss hitbox left edge differs from native bullet collision");
Check(bounds.Width == 80, "boss right-only extents lost half of the combat hitbox");
Check(bounds.Y == (int)CampaignRuntime.Origin.Y + 64 * 2 && bounds.Height == 48, "vertical combat bounds differ from native collision");
Check(CaveEntity.CombatBounds(Actor(5, true, left: 4)) == bounds, "terrain left extent changed bullet collision bounds");
Check(CaveEntity.CombatBounds(Actor(5, true, right: 0)).Width == 4, "degenerate native extents produce an invalid host target");
foreach (var screen in new[] { new Point(1280, 720), new Point(1919, 1079), new Point(960, 540) })
for (int scale = 2; scale <= 6; scale++)
{
    int width = Math.Max(160, screen.X / scale), height = Math.Max(120, screen.Y / scale);
    Rectangle output = CampaignView.OutputRectangle(screen.X, screen.Y, width, height, scale);
    var camera = new Vector2(117, 39);
    float zoom = scale / CampaignRuntime.Scale;
    Vector2 screenPosition = CampaignView.WorldScreenPosition(camera, screen.X, screen.Y, output, zoom);
    var transform = new SpriteViewMatrix(null!);
    transform.SetViewportOverride(new Viewport(0, 0, screen.X, screen.Y));
    transform.Zoom = new Vector2(zoom);
    Vector2 corner = Vector2.Transform(CampaignRuntime.Origin + camera * CampaignRuntime.Scale - screenPosition, transform.TransformationMatrix);
    Check(Vector2.Distance(corner, new Vector2(output.X, output.Y)) < .001f, "host camera origin differs from integer-scaled native output");
    Vector2 nextPixel = Vector2.Transform(CampaignRuntime.Origin + (camera + Vector2.UnitX) * CampaignRuntime.Scale - screenPosition, transform.TransformationMatrix);
    Check(Math.Abs(nextPixel.X - corner.X - scale) < .001f, "native pixel does not match host integer camera scale");
    Check(output.Width == width * scale && output.Height == height * scale, "native output stretches beyond its integer pixel scale");
}
string save = Path.Combine(Path.GetTempPath(), "caverarria-view-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(save);
try
{
    Terraria.Program.SavePath = save;
    ViewChecks.Run(Check);
}
finally { Directory.Delete(save, recursive: true); }
Console.WriteLine($"{checks} combat proxy regression checks passed against production targeting/geometry methods.");
