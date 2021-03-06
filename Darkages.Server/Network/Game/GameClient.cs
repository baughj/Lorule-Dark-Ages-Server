﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Darkages.Common;
using Darkages.Network.ServerFormats;
using Darkages.Scripting;
using Darkages.Storage;
using Darkages.Storage.locales.Scripts.Global;
using Darkages.Types;

namespace Darkages.Network.Game
{
    public class GameClient : NetworkClient
    {
        public List<uint> AnimationFactories = new List<uint>();
        public Collection<GlobalScript> GlobalScripts = new Collection<GlobalScript>();

        public object syncObj = new object();

        public GameServer Server { get; set; }

        public DateTime LastPing { get; set; }

        public DateTime LastSave { get; set; }

        public DateTime LastClientRefresh { get; set; }

        public bool IsRefreshing =>
            DateTime.Now - LastClientRefresh < new TimeSpan(0, 0, 0, 0, ServerContext.Config.RefreshRate);

        public GameServerTimer HpRegenTimer { get; set; }

        public GameServerTimer MpRegenTimer { get; set; }

        public Aisling Aisling { get; set; }

        public bool ShouldUpdateMap { get; set; }
        public DateTime LastMessageSent { get; set; }
        public DateTime LastPingResponse { get; set; }
        public byte LastActivatedLost { get; set; }


        public DialogSession DlgSession { get; set; }

        public bool IsDead()
        {
            return Aisling != null && Aisling.Flags == AislingFlags.Dead;
        }

        public bool CanSeeGhosts()
        {
            return IsDead();
        }

        public bool CanSeeHidden()
        {
            return Aisling != null && (Aisling.Flags & AislingFlags.SeeInvisible) == AislingFlags.SeeInvisible;
        }

        public void WarpTo(WarpTemplate warps)
        {
            if (warps.WarpType == WarpType.World)
                return;

            if (ServerContext.GlobalMapCache.Values.Any(i => i.ID == warps.ActivationMapId))
            {
                if (warps.LevelRequired > 0 && Aisling.ExpLevel < warps.LevelRequired)
                {
                    var msgTier = Math.Abs(Aisling.ExpLevel - warps.LevelRequired);

                    SendMessage(0x02,
                        msgTier <= 10 ? "You can't enter there just yet." : "You have nightmarish visions.");
                    return;
                }

                if (Aisling.Map.ID != warps.To.AreaID)
                {
                    LeaveArea(true, false);
                    Aisling.Map = ServerContext.GlobalMapCache[warps.To.AreaID];
                    Aisling.X = warps.To.Location.X;
                    Aisling.Y = warps.To.Location.Y;
                    Aisling.CurrentMapId = warps.To.AreaID;
                    Aisling.AreaID = Aisling.CurrentMapId;
                    EnterArea();
                    Aisling.Client.CloseDialog();
                }
                else
                {
                    LeaveArea(true, false);
                    Aisling.X = warps.To.Location.X;
                    Aisling.Y = warps.To.Location.Y;
                    EnterArea();
                    Aisling.Client.CloseDialog();
                }
            }
        }

        public void CloseDialog()
        {
            SendPacket(new byte[] { (byte)0x30, 0x00, 0x0A, 0x00 });
        }

        public void Update(TimeSpan elapsedTime)
        {
            #region Sanity Checks

            if (Aisling == null)
                return;

            if (!Aisling.LoggedIn)
                return;

            if ((DateTime.UtcNow - Aisling.LastLogged).TotalMilliseconds < ServerContext.Config.LingerState)
                return;

            //TODO: find out why this is disconnecting.
            //if ((DateTime.UtcNow - LastPingResponse).TotalSeconds > ServerContext.Config.Timeout)
            //{
            //    Server.ClientDisconnected(this);
            //    return;
            //}

            #endregion

            UpdateGlobalScripts(elapsedTime);

            Regeneration(elapsedTime);

            UpdateStatusBar(elapsedTime);

            SaveObject(Aisling);
        }

        private void UpdateStatusBar(TimeSpan elapsedTime)
        {
            Aisling.UpdateBuffs(elapsedTime);
            Aisling.UpdateDebuffs(elapsedTime);
        }

        private void UpdateGlobalScripts(TimeSpan elapsedTime)
        {
            foreach (var globalscript in GlobalScripts)
                if (globalscript != null && !(Aisling.Username.ToLower() == "godmode"))
                    globalscript.Update(elapsedTime);
        }

        private void Regeneration(TimeSpan elapsedTime)
        {
            if (HpRegenTimer == null)
                return;

            if (MpRegenTimer == null)
                return;

            var hpChanged = false;
            var mpChanged = false;

            if (Aisling.CurrentHp <= 0)
            {
                Aisling.CurrentHp = 0;
                hpChanged = true;
            }

            var HpregenRate = 200 / Aisling.Con * ServerContext.Config.RegenRate * 100 / 100 * 1000;
            var MpregenRate = 200 / Aisling.Wis * ServerContext.Config.RegenRate * 100 / 100 * 1000;

            if (Aisling.Con > Aisling.ExpLevel + 10)
                HpRegenTimer.Delay = TimeSpan.FromMilliseconds((1000 + HpregenRate) / 2);
            if (Aisling.Wis > Aisling.ExpLevel + 10)
                MpRegenTimer.Delay = TimeSpan.FromMilliseconds((1000 + MpregenRate) / 2);

            if (!HpRegenTimer.Disabled)
                HpRegenTimer.Update(elapsedTime);

            if (!MpRegenTimer.Disabled)
                MpRegenTimer.Update(elapsedTime);

            #region Hp Regen

            if (HpRegenTimer.Elapsed)
            {
                HpRegenTimer.Reset();

                if (Aisling.CurrentHp < Aisling.MaximumHp)
                {
                    hpChanged = true;

                    var hpRegenSeed = (Aisling.Con - Aisling.ExpLevel).Clamp(0, 10) * 0.01;
                    var hpRegenAmount = Aisling.MaximumHp * (hpRegenSeed + 0.10);

                    Aisling.CurrentHp = (Aisling.CurrentHp + (int) hpRegenAmount).Clamp(0, Aisling.MaximumHp);
                }
            }

            #endregion

            #region Mp Regen

            if (MpRegenTimer.Elapsed)
            {
                MpRegenTimer.Reset();

                if (Aisling.CurrentMp < Aisling.MaximumMp)
                {
                    mpChanged = true;

                    var mpRegenSeed = (Aisling.Wis - Aisling.ExpLevel).Clamp(0, 10) * 0.01;
                    var mpRegenAmount = Aisling.MaximumMp * (mpRegenSeed + 0.10);

                    Aisling.CurrentMp = (Aisling.CurrentMp + (int) mpRegenAmount).Clamp(0, Aisling.MaximumMp);
                }
            }

            #endregion

            if (!IsDead() && Aisling?.CurrentHp > 0)
            {
                if (!Aisling.LoggedIn)
                    return;

                if (hpChanged || mpChanged)
                    Send(new ServerFormat08(Aisling, StatusFlags.StructB));
            }
        }

        public bool Load()
        {
            LastPingResponse = DateTime.UtcNow;

            if (Aisling == null || Aisling.AreaID == 0)
                return false;

            if (!ServerContext.GlobalMapCache.ContainsKey(Aisling.AreaID))
                return false;

            try
            {
                LoadGlobalScripts();
                SetupRegenTimers();
                InitSpellBar();
                LoadInventory();
                LoadSkillBook();
                LoadSpellBook();
                LoadEquipment();
                SendProfileUpdate();
            }
            catch
            {
                return false;
            }

            return true;
        }

        private void LoadGlobalScripts()
        {
            GlobalScripts.Add(ScriptManager.Load<GrimReaper>("Grim Reaper", this));
        }

        private void SetupRegenTimers()
        {
            var HpregenRate = 200 / Aisling.Con * ServerContext.Config.RegenRate * 100 / 100 * 1000;
            var MpregenRate = 200 / Aisling.Wis * ServerContext.Config.RegenRate * 100 / 100 * 1000;

            HpRegenTimer = new GameServerTimer(
                TimeSpan.FromMilliseconds(1000 + HpregenRate));
            MpRegenTimer = new GameServerTimer(
                TimeSpan.FromMilliseconds(1000 + MpregenRate));
        }

        private void InitSpellBar()
        {
            for (var i = 0; i < Aisling.Buffs.Count; i++)
                Aisling.Buffs[i].Display(Aisling);

            for (var i = 0; i < Aisling.Debuffs.Count; i++)
                Aisling.Debuffs[i].Display(Aisling);
        }

        private void LoadEquipment()
        {
            foreach (var item in Aisling.EquipmentManager.Equipment)
            {
                var equipment = item.Value;

                if (equipment == null || equipment.Item == null || equipment.Item.Template == null)
                    continue;

                Send(new ServerFormat37(equipment.Item, (byte) equipment.Slot));
                equipment.Item.Script =
                    ScriptManager.Load<ItemScript>(equipment.Item.Template.ScriptName, equipment.Item);
                equipment.Item.Script?.Equipped(Aisling, (byte) equipment.Slot);

                if (Aisling.CurrentWeight <= Aisling.MaximumWeight)
                {
                    Aisling.CurrentWeight += equipment.Item.Template.Weight;
                }
                //for some reason, Aisling is out of Weight!
                else
                {
                    //clone and release item
                    var nitem = Clone(item.Value.Item);
                    nitem.Release(Aisling, Aisling.Position);

                    //display message
                    SendMessage(0x02, string.Format("You stumble and drop {0}", nitem.Template.Name));

                    continue;
                }

                if ((equipment.Item.Template.Flags & ItemFlags.Equipable) == ItemFlags.Equipable)
                    for (var i = 0; i < Aisling.SpellBook.Spells.Count; i++)
                    {
                        var spell = Aisling.SpellBook.FindInSlot(i);
                        if (spell != null)
                            equipment.Item.UpdateSpellSlot(this, spell.Slot);
                    }
            }
        }

        private void LoadSpellBook()
        {
            var spells_Available = Aisling.SpellBook.Spells.Values
                .Where(i => i != null && i.Template != null).ToArray();

            for (var i = 0; i < spells_Available.Length; i++)
            {
                var spell = spells_Available[i];
                spell.InUse = false;
                spell.NextAvailableUse = DateTime.UtcNow;
                Send(new ServerFormat17(spell));
                spell.Lines = spell.Template.BaseLines;
                spell.Script = ScriptManager.Load<SpellScript>(spell.Template.ScriptKey, spell);
                Aisling.SpellBook.Set(spell, false);
            }
        }

        private void LoadSkillBook()
        {
            var skills_Available = Aisling.SkillBook.Skills.Values
                .Where(i => i != null && i.Template != null).ToArray();

            for (var i = 0; i < skills_Available.Length; i++)
            {
                var skill = skills_Available[i];
                skill.InUse = false;
                skill.NextAvailableUse = DateTime.UtcNow;
                Send(new ServerFormat2C(skill.Slot,
                    skill.Icon,
                    skill.Name));

                skill.Script = ScriptManager.Load<SkillScript>(skill.Template.ScriptName, skill);
                Aisling.SkillBook.Set(skill, false);
            }
        }

        private void LoadInventory()
        {
            var items_Available = Aisling.Inventory.Items.Values
                .Where(i => i != null && i.Template != null).ToArray();

            for (var i = 0; i < items_Available.Length; i++)
            {
                var item = items_Available[i];
                item.Script = ScriptManager.Load<ItemScript>(item.Template.ScriptName, item);

                if (item.Template != null)
                {
                    Aisling.CurrentWeight += item.Template.Weight;

                    if (Aisling.CurrentWeight <= Aisling.MaximumWeight)
                    {
                        var format = new ServerFormat0F(item);
                        Send(format);
                        Aisling.Inventory.Set(item, false);
                    }
                    //for some reason, Aisling is out of Weight!
                    else
                    {
                        //clone and release item
                        var nitem = Clone(item);
                        nitem.Release(Aisling, Aisling.Position);

                        //display message
                        SendMessage(0x02, string.Format("You stumble and drop {0}", item.Template.Name));
                    }
                }
            }
        }

        public void UpdateDisplay()
        {
            //construct display Format for dispatching out.
            var response = new ServerFormat33(this, Aisling);

            //Display Aisling to self.
            Aisling.Show(Scope.Self, response);

            //Display Aisling to everyone else nearby.
            if (Aisling.Flags == AislingFlags.Dead)
            {
                //only show to clients who can see ghosts.
                var nearby = GetObjects<Aisling>(i => i.WithinRangeOf(Aisling) && i.Client.CanSeeGhosts());
                Aisling.Show(Scope.NearbyAislingsExludingSelf, response, nearby);
            }
            else
            {
                //show to everyone except myself.
                Aisling.Show(Scope.NearbyAislingsExludingSelf, response);
            }
        }

        public void Refresh(bool delete = false)
        {
            LeaveArea(delete);
            EnterArea();
        }

        public void LeaveArea(bool update = false, bool delete = false)
        {
            Aisling.LastMapId = Aisling.CurrentMapId;
            Aisling.Remove(update, delete);
        }

        public void EnterArea()
        {
            SendSerial();
            Insert();
            RefreshMap();
            SendLocation();
            UpdateDisplay();
            RefreshObjects();


        }

        public void SendMusic()
        {
            Aisling.Client.SendPacket(new byte[] {
                    0x19, 0x00, 0xFF,
                    (byte)Aisling.Map.Music });
        }

        public void Insert()
        {
            if (Aisling.Map.Tile == null)
                return;

            if (GetObject<Aisling>(i => i.Serial == Aisling.Serial) == null)
                AddObject(Aisling);

            Aisling.Map.Tile[Aisling.X, Aisling.Y] = TileContent.Aisling;
        }

        public void RefreshMap()
        {
            if (Aisling.CurrentMapId != Aisling.LastMapId)
            {
                ShouldUpdateMap = true;
                Aisling.LastMapId = Aisling.CurrentMapId;
                SendMusic();
            }

            if (ShouldUpdateMap)
            {
                Aisling.ViewFrustrum = new List<Sprite>();
                Send(new ServerFormat15(Aisling.Map));
            }
        }

        public void RefreshObjects()
        {
            //Show Nearby Objects to self.
            var nearbyobjs = GetObjects(i => i.WithinRangeOf(Aisling), Get.All);
            foreach (var obj in nearbyobjs)
            {
                if (obj is Aisling)
                    continue;

                if (obj is Monster)
                    SaveObject(obj as Monster);

                if (obj is Money)
                    SaveObject(obj as Money);

                if (obj is Item)
                    SaveObject(obj as Item);

                if (obj is Mundane)
                    SaveObject(obj as Mundane);
            }
        }

        private void SendSerial()
        {
            //send Serial
            Send(new ServerFormat05(Aisling));
        }

        public void SendLocation()
        {
            //send position
            Send(new ServerFormat04(Aisling));
        }

        public void Save()
        {
            //Ensure we are saved on the correct map.
            if (Aisling.AreaID != Aisling.CurrentMapId)
                Aisling.AreaID = Aisling.CurrentMapId;

            SaveObject(Aisling);
            StorageManager.AislingBucket.Save(Aisling);
            LastSave = DateTime.UtcNow;
        }

        public void SendMessage(byte type, string text)
        {
            Send(new ServerFormat0A(type, text));
            LastMessageSent = DateTime.UtcNow;
        }

        public void SendMessage(Scope scope, byte type, string text)
        {
            switch (scope)
            {
                case Scope.Self:
                    SendMessage(type, text);
                    break;
                case Scope.NearbyAislings:
                {
                    var nearby = GetObjects<Aisling>(i => i.WithinRangeOf(Aisling));

                    foreach (var obj in nearby)
                        obj.Client.SendMessage(type, text);
                }
                    break;
                case Scope.NearbyAislingsExludingSelf:
                {
                    var nearby = GetObjects<Aisling>(i => i.WithinRangeOf(Aisling));

                    foreach (var obj in nearby)
                    {
                        if (obj.Serial == Aisling.Serial)
                            continue;

                        obj.Client.SendMessage(type, text);
                    }
                }
                    break;
                case Scope.AislingsOnSameMap:
                {
                    var nearby = GetObjects<Aisling>(i => i.WithinRangeOf(Aisling)
                                                          && i.CurrentMapId == Aisling.CurrentMapId);

                    foreach (var obj in nearby)
                        obj.Client.SendMessage(type, text);
                }
                    break;
            }
        }

        public void Say(string message, byte type = 0x00)
        {
            var response = new ServerFormat0D
            {
                Serial = Aisling.Serial,
                Type = type,
                Text = message
            };

            Aisling.Show(Scope.NearbyAislings, response);
        }

        public void SendAnimation(ushort Animation, Sprite To, Sprite From, byte speed = 100, bool repeat = false)
        {
            var format = new ServerFormat29((uint) From.Serial, (uint) To.Serial, Animation, 0, speed);

            if (!repeat)
            {
                Aisling.Show(Scope.NearbyAislings, format);
                return;
            }

            if (To is Aisling)
                new TaskFactory().StartNew(() =>
                {
                    while (true)
                    {
                        if (!Aisling.InsideView(To))
                            break;

                        (To as Aisling).Show(Scope.NearbyAislings, format);

                        Thread.Sleep(1000);
                    }
                });
        }

        public void SendItemShopDialog(Mundane mundane, string text, ushort step, IEnumerable<ItemTemplate> items)
        {
            Send(new ServerFormat2F(mundane, text, new ItemShopData(step, items)));
        }

        public void SendItemSellDialog(Mundane mundane, string text, ushort step, IEnumerable<byte> items)
        {
            Send(new ServerFormat2F(mundane, text, new ItemSellData(step, items)));
        }

        public void SendOptionsDialog(Mundane mundane, string text, params OptionsDataItem[] options)
        {
            Send(new ServerFormat2F(mundane, text, new OptionsData(options)));
        }

        public void SendOptionsDialog(Mundane mundane, string text, string args, params OptionsDataItem[] options)
        {
            Send(new ServerFormat2F(mundane, text, new OptionsPlusArgsData(options, args)));
        }

        public void SendSkillLearnDialog(Mundane mundane, string text, ushort step, IEnumerable<SkillTemplate> skills)
        {
            Send(new ServerFormat2F(mundane, text, new SkillAcquireData(step, skills)));
        }

        public void SendSpellLearnDialog(Mundane mundane, string text, ushort step, IEnumerable<SpellTemplate> spells)
        {
            Send(new ServerFormat2F(mundane, text, new SpellAcquireData(step, spells)));
        }

        public void SendSkillForgetDialog(Mundane mundane, string text, ushort step)
        {
            Send(new ServerFormat2F(mundane, text, new SkillForfeitData(step)));
        }

        public void SendSpellForgetDialog(Mundane mundane, string text, ushort step)
        {
            Send(new ServerFormat2F(mundane, text, new SpellForfeitData(step)));
        }

        public void SendStats(StatusFlags flags)
        {
            Send(new ServerFormat08(Aisling, flags));
        }

        public void SendProfileUpdate()
        {
            SendPacket(new byte[] { 73, 0x00 });
        }

        public void TrainSpell(Spell spell)
        {
            if (spell.Level < spell.Template.MaxLevel)
            {
                var toImprove = (int)(1.5 * spell.Level * spell.Template.LevelRate);
                if (spell.Casts++ >= toImprove)
                {
                    spell.Level++;
                    spell.Casts = 0;
                    Send(new ServerFormat17(spell));
                    SendMessage(0x02, string.Format("{0} has improved!", spell.Template.Name));
                }
            }
        }

        public void TrainSkill(Skill skill)
        {
            if (skill.Level < skill.Template.MaxLevel)
            {
                var toImprove = (int)(1.5 * skill.Level * skill.Template.LevelRate);
                if (skill.Uses++ >= toImprove)
                {
                    skill.Level++;
                    skill.Uses = 0;
                    Send(new ServerFormat2C(skill.Slot, skill.Icon, skill.Name));
                    SendMessage(0x02, string.Format("{0} has improved!", skill.Template.Name));
                }
            }
        }

        /// <summary>
        ///     Stop and Interupt everything this client is doing.
        /// </summary>
        public void Interupt()
        {
            GameServer.CancelIfCasting(this);
            SendLocation();
        }

        public bool CheckReqs(GameClient client, Item item)
        {
            if (client.Aisling.ExpLevel >= item.Template.LevelRequired
                && (client.Aisling.Path == item.Template.Class || item.Template.Class == Class.Peasant))
            {
                if (item.Template.Gender == Gender.Both)
                {
                    client.Aisling.EquipmentManager.Add(item.Template.EquipmentSlot, item);
                }
                else
                {
                    if (item.Template.Gender == client.Aisling.Gender)
                    {
                        client.Aisling.EquipmentManager.Add(item.Template.EquipmentSlot, item);
                    }
                    else
                    {
                        client.SendMessage(0x02, ServerContext.Config.DoesNotFitMessage);
                        return false;
                    }
                }

                return true;
            }

            client.SendMessage(0x02, ServerContext.Config.CantEquipThatMessage);
            return false;
        }
    }
}