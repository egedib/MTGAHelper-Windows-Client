﻿
using MTGAHelper.Entity;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MTGAHelper.Lib.Config
{
    public interface IConfigModel
    {
        string Id { get; }
    }

    [Serializable]
    public class ConfigModelDeck : ConfigModelRawDeck, IConfigModel
    {
        public const string SOURCE_SYSTEM = "automatic";
        public const string SOURCE_USERCUSTOM = "usercustom";
        public string Id { get; set; }
        public DateTime DateScrapedUtc { get; set; }
        public DateTime DateCreatedUtc { get; set; }
        public string ScraperTypeId { get; set; }
        public int? ScraperTypeOrderIndex { get; set; }
        public string Url { get; set; }
        public string UrlDeckList { get; set; }

        [JsonIgnore]
        public IDeck Deck { get; set; }

        public ConfigModelDeck()
        {
        }

        public ConfigModelDeck(IDeck deck, string url, DateTime dateCreatedUtc)
        {
            Deck = deck;
            ScraperTypeId = deck.ScraperType.Id;
            Id = Deck.Id;
            Name = deck.Name;
            DateScrapedUtc = DateTime.UtcNow;
            Url = url ?? "";
            DateCreatedUtc = dateCreatedUtc;
        }

        public ICollection<DeckCard> GetCards(ICollection<Card> allCards)
        {
            var cardsMain = CardsMain.Select(i => new DeckCard(
                new CardWithAmount(allCards.First(x => x.grpId == i.Key), i.Value), false));
            var cardsSideboard = CardsSideboard.Select(i => new DeckCard(
                new CardWithAmount(allCards.First(x => x.grpId == i.Key), i.Value), true));

            return cardsMain.Union(cardsSideboard).ToArray();
        }
    }
}