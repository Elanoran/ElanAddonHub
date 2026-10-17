using System;
using System.Collections.Generic;
using System.Linq;

namespace ElansAddonHub.Services.Atlas
{
    // ============================================================================================================================
    // The tabs of an entity page (Overview | Spells | Racial abilities | Sources | Map | Related | Lodge) and what they do to an id. Spells is a class page's
    // spellbook (2.85, it was a card on the Overview), Racial abilities a race page's racials and race-only class spells (2.85, they were on Related).
    //   item:100                 the page, on the tab the kind opens on (a zone on Map, everything else on Overview)
    //   item:100?tab=sources     the SAME page, on its Sources tab (a link can point at a tab: chat, Copy link, pins, recents)
    // The tab is a way of looking at the page, not another page: for identity (recents, pins, the breadcrumb, Follow, "is this the page I am on")
    // the two are one thing, BaseId() says which; the id with its tab is kept for navigation and for Copy link. Changing the tab on the page
    // replaces the history entry instead of adding one (Back goes to the page before, not to the tab before). Only entity ids (item:, npc:, ...) take
    // a tab; browse ids keep their own filters after the "?" and hosted ids their own paths.
    // ============================================================================================================================
    public static class AtlasTabs
    {
        public const string Overview = "overview", Spells = "spells", Abilities = "abilities", Sources = "sources", Map = "map", Related = "related", Lodge = "lodge";
        public static readonly string[] Order = { Overview, Spells, Abilities, Sources, Map, Related, Lodge };
        const string Query = "?tab=";

        public static string Title(string tab)
        {
            switch (tab)
            {
                case Overview: return "Overview";
                case Spells: return "Spells";
                case Abilities: return "Racial abilities";
                case Sources: return "Sources";
                case Map: return "Map";
                case Related: return "Related";
                case Lodge: return "Lodge";
                default: return tab ?? "";
            }
        }

        public static bool IsTab(string tab) => !string.IsNullOrEmpty(tab) && Array.IndexOf(Order, tab) >= 0;

        // "item:100?tab=sources" -> "item:100" + "sources". False (and the id unchanged) when the id has no valid tab. An unknown tab word is dropped (the page opens on its default).
        public static bool Split(string id, out string baseId, out string tab)
        {
            baseId = id ?? ""; tab = "";
            if (string.IsNullOrEmpty(id)) return false;
            int q = id.IndexOf('?');
            if (q <= 0) return false;
            var head = id.Substring(0, q);
            if (!IsEntityHead(head)) return false;
            var rest = id.Substring(q + 1);
            foreach (var part in rest.Split('&'))
            {
                if (part.StartsWith("tab=", StringComparison.OrdinalIgnoreCase)) { var t = part.Substring(4).Trim().ToLowerInvariant(); if (IsTab(t)) tab = t; }
            }
            baseId = head;
            return true;
        }

        // "kind:digits" for an entity kind (no tab, no filters)
        static bool IsEntityHead(string s)
        {
            int c = s.IndexOf(':');
            if (c <= 0 || c == s.Length - 1) return false;
            var kind = s.Substring(0, c);
            if (Array.IndexOf(AtlasKinds.Order, kind) < 0) return false;
            for (int i = c + 1; i < s.Length; i++) if (s[i] < '0' || s[i] > '9') return false;
            return true;
        }

        // the identity of a page: its id without the tab. Everything that asks "is this the same page" compares these.
        public static string BaseId(string id) { Split(id, out var b, out _); return b; }
        public static string TabOf(string id) { Split(id, out _, out var t); return t; }
        public static bool SamePage(string a, string b) => string.Equals(BaseId(a), BaseId(b), StringComparison.Ordinal);

        // the id with a tab. The tab the kind opens on is left out when it is the default, so the plain id stays the plain id.
        public static string WithTab(string id, string tab)
        {
            var b = BaseId(id);
            if (!IsEntityHead(b) || !IsTab(tab)) return b;
            return b + Query + tab;
        }

        // the tab a kind opens on when the id names none
        public static string DefaultFor(string kind) => kind == "zone" ? Map : Overview;

        // which tab a section of the page model lives on (the AtlasSection.Key values): Sources = where it comes from, Lodge = what the Lodge added, Overview = a quest's own lists and "Your characters", Related = the rest
        public static string OfSection(string key)
        {
            switch (key)
            {
                case "sources": case "drops": case "givers": case "enders": case "nodes": return Sources;
                case "finds": case "crafters": case "lodgenotes": case "standing-lodge": return Lodge;
                case "racial": case "raceonly": return Abilities;      // a race page: its racial abilities and the class spells only it gets (2.85)
                case "owned": case "rewards": case "choices": case "objectives": case "standing-mine": return Overview;      // what a quest gives and asks for is the quest; a faction: where your characters stand
                default: return Related;
            }
        }
    }
}
