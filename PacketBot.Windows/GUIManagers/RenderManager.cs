using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.Http;
using System.IO;

namespace FunnyProject.GUIManagers
{
    public class RenderManager : IDisposable
    {
        // RenderManager is recreated every frame: cache the image across instances.
        private static readonly HttpClient minimapClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        private static readonly object minimapSync = new object();
        private static readonly Dictionary<int, Bitmap?> minimapCache = new Dictionary<int, Bitmap?>();
        private static readonly HashSet<int> minimapLoading = new HashSet<int>();

        private static void EnsureMinimapLoaded(int mapId)
        {
            if (mapId <= 0) return;
            lock (minimapSync)
            {
                if (minimapCache.ContainsKey(mapId) || !minimapLoading.Add(mapId))
                    return;
            }
            _ = Task.Run(async () =>
            {
                Bitmap? bitmap = null;
                try
                {
                    string[] urls =
                    {
                        $"https://wordgalaxy.space/spacemap/graphics/minimaps/minimap-{mapId}-700.jpg",
                        $"https://wordgalaxy.space/spacemap/graphics/minimaps/minimap-{mapId}-300.jpg",
                        $"https://wordgalaxy.space/spacemap/graphics/minimaps/minimap-{mapId}.jpg"
                    };
                    foreach (string url in urls)
                    {
                        try
                        {
                            using var response = await minimapClient.GetAsync(url);
                            if (!response.IsSuccessStatusCode) continue;
                            byte[] data = await response.Content.ReadAsByteArrayAsync();
                            using var stream = new MemoryStream(data);
                            using var decoded = Image.FromStream(stream);
                            bitmap = new Bitmap(decoded);
                            break;
                        }
                        catch (Exception)
                        {
                            // Try the next available minimap variant.
                        }
                    }
                }
                catch (Exception)
                {
                    // Missing/unavailable minimaps intentionally use black background.
                }
                finally
                {
                    lock (minimapSync)
                    {
                        minimapCache[mapId] = bitmap;
                        minimapLoading.Remove(mapId);
                    }
                }
            });
        }

        private void DrawMinimapBackground()
        {
            int mapId = Api.User.UserData.MapId;
            EnsureMinimapLoaded(mapId);
            lock (minimapSync)
            {
                if (minimapCache.TryGetValue(mapId, out Bitmap? image) && image != null)
                {
                    g.DrawImage(image, new Rectangle(0, 0, X, Y));
                    return;
                }
            }
            g.Clear(Color.Black);
        }

        Graphics g { get; set; }
        Api Api { get; set; }
        double ConvertX, ConvertY;
        private int X;
        private int Y;
        public RenderManager(Graphics g, Api api ,  int x , int y)
        {
            Api = api;
            ConvertX = ((double)x / 21000);
            ConvertY = (double)y / 13000;
            this.g = g;
            X = x;
            Y = y;
            
        }
        private async Task DrawTarget()
        {
            if (Api.User.Target == null) return;

            g.FillEllipse(Brushes.OrangeRed,ReturnFloat(Api.User.Target.X * ConvertX), ReturnFloat(Api.User.Target.Y * ConvertY), 10, 10);
            await Task.CompletedTask;


        }

        private async Task DrawPlayer()
        {
            g.FillEllipse(Brushes.White , ReturnFloat(Api.User.Position.X * ConvertX) , ReturnFloat(Api.User.Position.Y * ConvertY) - 10 , 10 , 10);
            await Task.CompletedTask;
        }
        private async Task DrawPorts()
        {
            lock(Api.User.BasesPorts.Ports)
            {
                foreach (var port in Api.User.BasesPorts.Ports)
                {
                    g.DrawEllipse(new Pen(Brushes.White), (float)(port.X * ConvertX), (float)(port.Y * ConvertY) - 8 , 16 , 16 );
                 
                }
            }
            await Task.CompletedTask;
        }
        private async Task DrawBoxes()
        {
            lock(Api.User.Boxes)
            {
                foreach (var box in Api.User.Boxes._Boxes)
                {
                    if(box.Type.Contains("BONUS"))
                        g.FillEllipse(Brushes.Yellow, (float)(box.X * ConvertX) + 2, (float)(box.Y * ConvertY) -6 , 4, 4);
                }
            }
            await Task.CompletedTask;
        }
        private async Task RenderPlayerHp()
        {
            string heroName = Api.User.UserData.InGameName;
            if (!string.IsNullOrWhiteSpace(heroName))
            {
                using var font = new Font(SystemFonts.DefaultFont.FontFamily, 9, FontStyle.Bold);
                g.DrawString(heroName, font, Brushes.White, 10, Y - 67);
            }
            g.DrawLine(new Pen(Brushes.DarkGreen, 15), 10, Y - 40, 210, Y - 40);
            var hp = (int)((double)Api.User.ShipInfo.Hp / Api.User.ShipInfo.MaxHp * 100);
            g.DrawLine(new Pen(Brushes.LightGreen , 15) , 210 ,Y-40 , 210 - (2 * (100 - hp)),Y-40);
            g.DrawLine(new Pen(Brushes.DarkBlue, 15), 10, Y - 18 , 210 , Y-18);
            int shd = 0;
            if(Api.User.ShipInfo.Shield == 0 || Api.User.ShipInfo.MaxShield == 0)
            {
                shd = 100;
            }
            else
            {
                shd= (int)((double)Api.User.ShipInfo.Shield / Api.User.ShipInfo.MaxShield * 100);
            }
            g.DrawLine(new Pen(Brushes.LightBlue, 15), 210, Y - 18, 210 - (2 * (100 - shd)), Y - 18);
            await Task.CompletedTask;
        }

        private async Task RenderNpcHp()
        {
            var target = Api.User.Target;
            if (target == null) return;
            if (!string.IsNullOrWhiteSpace(target.Name))
            {
                using var font = new Font(SystemFonts.DefaultFont.FontFamily, 9, FontStyle.Bold);
                var size = g.MeasureString(target.Name, font);
                g.DrawString(target.Name, font, Brushes.White, X - 10 - size.Width, Y - 67);
            }
            if (!Api.User.TargetConfirmed)
            {
                // The server has not confirmed the selection (packet 11468).
                using var font = new Font(SystemFonts.DefaultFont.FontFamily, 8, FontStyle.Bold);
                const string text = "sin confirmar";
                var size = g.MeasureString(text, font);
                g.DrawString(text, font, Brushes.Gold, X - 10 - size.Width, Y - 84);
            }

            // The NPC's initial spawn packet does not include HP/shield.
            // Avoid displaying false or inverted bars until real data arrives.
            if (target.MaxHP > 0)
            {
                double hp = Math.Clamp((double)target.HP / target.MaxHP, 0, 1);
                g.DrawLine(new Pen(Brushes.IndianRed, 15), X - 210, Y - 40, X - 10, Y - 40);
                g.DrawLine(new Pen(Brushes.DarkRed, 15), X - 210, Y - 40,
                    X - 210 + (float)(200 * hp), Y - 40);
            }
            if (target.MaxShield > 0)
            {
                double shield = Math.Clamp((double)target.Shield / target.MaxShield, 0, 1);
                g.DrawLine(new Pen(Brushes.LightBlue, 15), X - 210, Y - 18, X - 10, Y - 18);
                g.DrawLine(new Pen(Brushes.DarkBlue, 15), X - 210, Y - 18,
                    X - 210 + (float)(200 * shield), Y - 18);
            }
            await Task.CompletedTask;
        }
        // Barriers (NoAccess zones) in translucent red, as the game collides
        // with them. Inverted zones block a band of Margin around them (a
        // thick wall when the zone is a line or a point): fill that band.
        private void DrawBarriers()
        {
            using var fill = new SolidBrush(Color.FromArgb(90, 255, 60, 60));
            using var edge = new Pen(Color.FromArgb(220, 255, 80, 80), 1f);
            foreach (var z in Api.User.Zones.Barriers())
            {
                using var path = new System.Drawing.Drawing2D.GraphicsPath(System.Drawing.Drawing2D.FillMode.Alternate);
                if (z.Shape == FunnyProject.UserCollections.PoiShape.Circle)
                {
                    int r = z.Inverted ? z.Radius + z.Margin : z.Radius;
                    path.AddEllipse(Scale(z.CenterX - r, z.CenterY - r, 2 * r, 2 * r));
                    if (z.Inverted && z.Radius > 0)
                        path.AddEllipse(Scale(z.CenterX - z.Radius, z.CenterY - z.Radius, 2 * z.Radius, 2 * z.Radius));
                }
                else
                {
                    int k = z.Inverted ? z.Margin : 0;
                    path.AddRectangle(Scale(z.Left - k, z.Top - k, z.Width + 2 * k, z.Height + 2 * k));
                    if (z.Inverted && z.Width > 0 && z.Height > 0)
                        path.AddRectangle(Scale(z.Left, z.Top, z.Width, z.Height));
                }
                g.FillPath(fill, path);
                g.DrawPath(edge, path);
            }
        }

        // NPC Killer preferred zones of the current map (game coordinates).
        public FunnyProject.Logic.PreferredZone[] PreferredZones { get; set; } = Array.Empty<FunnyProject.Logic.PreferredZone>();
        // Zone being dragged on the minimap, if any.
        public Rectangle? ZonePreview { get; set; }

        private void DrawPreferredZones()
        {
            using var fill = new SolidBrush(Color.FromArgb(46, 60, 220, 120));
            using var edge = new Pen(Color.FromArgb(240, 90, 240, 140), 1.5f);
            using var font = new Font(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
            using var text = new SolidBrush(Color.FromArgb(240, 90, 240, 140));
            for (int i = 0; i < PreferredZones.Length; i++)
            {
                var z = PreferredZones[i];
                var r = Scale(z.X, z.Y, z.Width, z.Height);
                g.FillRectangle(fill, r);
                g.DrawRectangle(edge, r.X, r.Y, r.Width, r.Height);
                g.DrawString((i + 1).ToString(), font, text, r.X + 2, r.Y + 2);
            }
            if (ZonePreview is Rectangle p)
            {
                using var dashed = new Pen(Color.FromArgb(240, 90, 240, 140), 1.5f)
                    { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
                var r = Scale(p.X, p.Y, p.Width, p.Height);
                g.FillRectangle(fill, r);
                g.DrawRectangle(dashed, r.X, r.Y, r.Width, r.Height);
            }
        }

        // Stations of the current map (bases, repair docks, hangars…).
        private void DrawStations()
        {
            var stations = Api.User.Stations.For(Api.User.UserData.MapId);
            if (stations.Length == 0) return;
            using var fill = new SolidBrush(Color.FromArgb(200, 70, 140, 255));
            using var edge = new Pen(Color.White, 1f);
            using var font = new Font(FontFamily.GenericSansSerif, 7f, FontStyle.Bold);
            foreach (var s in stations)
            {
                float x = (float)(s.X * ConvertX), y = (float)(s.Y * ConvertY);
                g.FillRectangle(fill, x - 5, y - 5, 10, 10);
                g.DrawRectangle(edge, x - 5, y - 5, 10, 10);
                var size = g.MeasureString(s.Short, font);
                g.DrawString(s.Short, font, Brushes.White, x - size.Width / 2, y - 7 - size.Height);
            }
        }

        private RectangleF Scale(int x, int y, int w, int h) =>
            new RectangleF((float)(x * ConvertX), (float)(y * ConvertY), (float)(w * ConvertX), (float)(h * ConvertY));

        private async Task DrawTrajectory()
        {
            g.DrawLine(new Pen(Brushes.White) , (float)(Api.User.Position.X * ConvertX) + 5 , (float)(Api.User.Position.Y *ConvertY) -5,(float)(Api.User.Position.TargetX * ConvertX) +5 ,(float)(Api.User.Position.TargetY * ConvertY)-5);
           
            await Task.CompletedTask;
        }

        
        private async Task DrawPet()
        {
            if (Api.User.PetMap.TryGetPosition(out int petX, out int petY))
            {
                float x = (float)(petX * ConvertX);
                float y = (float)(petY * ConvertY);
                g.FillEllipse(Brushes.Cyan, x - 4, y - 4, 9, 9);
                g.DrawEllipse(Pens.White, x - 4, y - 4, 9, 9);
            }
            await Task.CompletedTask;
        }

        private async Task DrawNpcs()
        {
            lock(Api.User.Players.Npcs)
            {
                foreach (var npc in Api.User.Players.Npcs)
                {
                    g.FillEllipse(Brushes.Red,(float)(npc.X * ConvertX) , (float)(npc.Y * ConvertY), 4, 4);
                }
            }
            await Task.CompletedTask;
        }
        private async Task DrawEnemyPlayers()
        {
            lock (Api.User.Players.EnemyPlayers)
            {
                foreach (var enemy in Api.User.Players.EnemyPlayers)
                {
                    g.FillEllipse(Brushes.Red, (float)(enemy.X * ConvertX), (float)(enemy.Y * ConvertY) , 7 , 7);
                }
            }
            await Task.CompletedTask;
        }
    
        private async Task DrawAllyPlayers()
        {
            lock (Api.User.Players.AllyPlayers)
            {
                foreach (var ally in Api.User.Players.AllyPlayers)
                {
                    g.FillEllipse(Brushes.Blue, (float)(ally.X *ConvertX), (float)(ally.Y * ConvertY), 4, 4);
                }
            }
            await Task.CompletedTask;
        }

        private List<Task> Tasks = new();
        public async Task Draw()
        {
            DrawMinimapBackground();
            DrawBarriers();
            DrawPreferredZones();
            DrawStations();
            if(Api.User.Position.Moving)
            {
                Tasks.Add(DrawTrajectory());
            }
            Tasks.Add(RenderPlayerHp());
            Tasks.Add(DrawBoxes());
            Tasks.Add(DrawPorts());
            Tasks.Add(DrawNpcs());
            Tasks.Add(DrawPet());
            Tasks.Add(DrawEnemyPlayers());
            Tasks.Add(DrawAllyPlayers());
            Tasks.Add(DrawTarget());
            Tasks.Add(RenderNpcHp());
            
            
            Tasks.Add(DrawPlayer());


            
            await Task.WhenAll(Tasks);
        }
        
       
        private float ReturnFloat(object num)
        {
            return Convert.ToSingle(num);
        }

        public  void Dispose()
        {
            g.Dispose();
        }
    }
}
