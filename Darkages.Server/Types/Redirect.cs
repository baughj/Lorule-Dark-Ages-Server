﻿using Darkages.Network.Game;

namespace Darkages.Types
{
    public class Redirect
    {
        public int ID;
        public string Name;
        public byte Seed;
        public byte[] Salt;
        public int Serial;

        public int Type { get; internal set; }
        public GameClient Client { get; internal set; }
    }
}
