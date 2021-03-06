﻿using System.Collections.Generic;

namespace MTGAHelper.Lib.IO.Reader.MtgaOutputLog.GRE.MatchToClient.SubmitDeckReq.Raw
{
    public class SubmitDeckReqRaw : GreMatchToClientSubMessageBase
    {
        public SubmitDeckReq submitDeckReq { get; set; }
    }

    public class Deck : IDeckMessage
    {
        public List<int> deckCards { get; set; }
        public List<int> sideboardCards { get; set; }
    }

    public class SubmitDeckReq
    {
        public Deck deck { get; set; }
    }
}
