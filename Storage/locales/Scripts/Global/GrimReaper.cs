﻿using Darkages.Network.Game;
using Darkages.Network.ServerFormats;
using Darkages.Scripting;
using Darkages.Types;
using System;

namespace Darkages.Storage.locales.Scripts.Global
{
    [Script("Grim Reaper", "Dean")]
    public class GrimReaper : GlobalScript
    {
        GameClient Client;
        GameServerTimer GrimTimer;

        public GrimReaper(GameClient client) : base(client)
        {
            GrimTimer = new GameServerTimer(TimeSpan.FromMilliseconds(ServerContext.Config.HealoutTolerance));
            Client = client;
        }

        public override void OnDeath(GameClient client, TimeSpan Elapsed)
        {
            if (client == null || client.Aisling == null)
                return;

            if (!client.Aisling.Dead)
            {
                lock (client.Aisling)
                {
                    if (client.Aisling.CurrentHp > 0)
                    {
                        client.SendStats(StatusFlags.All);
                        return;
                    }

                    client.Aisling.Flags = AislingFlags.Dead;
                    client.HpRegenTimer.Disabled = true;
                    client.MpRegenTimer.Disabled = true;

                    client.Aisling.RemoveAllBuffs();
                    client.Aisling.RemoveAllDebuffs();

                    client.Send(new ServerFormat08(client.Aisling,
                        Darkages.Types.StatusFlags.All));

                    Client.LeaveArea(true, false);
                    client.EnterArea();

                    if (client.Aisling.Map.Flags.HasFlag(MapFlags.PlayerKill))
                    {
                        var target = client.Aisling.Target;

                        if (target != null)
                        {
                            if (target is Aisling)
                            {
                                client.SendMessage(Scope.NearbyAislings, 0x02, client.Aisling.Username + " has been killed by " + (target as Aisling).Username);
                            }
                        }
                        else
                        {
                            client.SendMessage(Scope.NearbyAislings, 0x02, client.Aisling.Username + " has been killed.");
                        }

                        return;
                    }

                    SendToHell(client);
                }
            }
        }

        private static void SendToHell(GameClient client)
        {
            client.Aisling.Show(Scope.NearbyAislings,
                (new ServerFormat29((uint)client.Aisling.Serial,
                (uint)client.Aisling.Serial, 0x81, 0x81, 100)));

            client.LeaveArea(true, true);
            client.Aisling.X = 13;
            client.Aisling.Y = 11;
            client.Aisling.Direction = 0;
            client.Aisling.CurrentMapId = 88888;
            client.Aisling.Map = ServerContext.GlobalMapCache[88888];
            client.Save();
            client.ShouldUpdateMap = true;
            client.EnterArea();
        }

        public override void Update(TimeSpan elapsedTime)
        {
            if (Client != null)
            {
                if (Client.Aisling.CurrentHp <= 0)
                {
                    Client.Aisling.CurrentHp = 0;

                    GrimTimer.Update(elapsedTime);

                    if (GrimTimer.Elapsed)
                    {
                        OnDeath(Client, elapsedTime);
                        GrimTimer.Reset();
                    }
                }
                else
                {
                    if (Client.Aisling.Dead)
                    {
                        Revive();
                    }
                }
            }            
        }

        private void Revive()
        {
            Client.HpRegenTimer.Disabled = false;
            Client.MpRegenTimer.Disabled = false;
            Client.Aisling.CurrentHp = Client.Aisling.MaximumHp;
            Client.Aisling.Flags = AislingFlags.Normal;
            Client.Send(new ServerFormat08(Client.Aisling,
                Darkages.Types.StatusFlags.All));
            Client.UpdateDisplay();
            Client.Aisling.Show(Scope.NearbyAislings,
                (new ServerFormat29((uint)Client.Aisling.Serial,
                (uint)Client.Aisling.Serial, 160, 160, 100)));
            Client.Aisling.Flags = AislingFlags.Normal;
            Client.Save();
        }
    }
}
