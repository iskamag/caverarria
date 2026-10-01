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

Check(CaveEntity.IsCombatTarget(Actor(0, true)), "zero-local-HP vulnerable boss part lost its host target");
Check(CaveEntity.IsCombatTarget(Actor(5, true)), "ordinary shootable enemy lost its host target");
Check(!CaveEntity.IsCombatTarget(Actor(1000, false)), "closed/invulnerable native phase became damageable");
var bounds = CaveEntity.CombatBounds(Actor(0, true));
Check(bounds.X == (int)CampaignRuntime.Origin.X + 80 * 3, "boss hitbox left edge differs from native bullet collision");
Check(bounds.Width == 120, "boss right-only extents lost half of the combat hitbox");
Check(bounds.Y == (int)CampaignRuntime.Origin.Y + 64 * 3 && bounds.Height == 72, "vertical combat bounds differ from native collision");
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
Console.WriteLine($"{checks} combat proxy regression checks passed against production targeting/geometry methods.");
