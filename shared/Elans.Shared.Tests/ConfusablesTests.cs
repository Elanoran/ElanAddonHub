using System.Globalization;
using Elans.Shared;

namespace Elans.Shared.Tests;

// Confusables: the UTS #39 skeleton over the generated table (Confusables.Data.cs) and the name key built on it
public class ConfusablesTests
{
    static string RepoFile(params string[] parts)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(new[] { d.FullName }.Concat(parts).ToArray());
            if (File.Exists(p)) return p;
        }
        return null;
    }

    [Fact]
    public void The_table_holds_every_mapping_of_confusables_txt_exactly()
    {
        var path = RepoFile("Tools", "unicode", "confusables.txt");
        Assert.NotNull(path);
        int n = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var cols = line.Split('#')[0].Split(';').Select(c => c.Trim()).ToArray();
            if (cols.Length < 3 || cols[2] != "MA") continue;
            var src = int.Parse(cols[0], NumberStyles.HexNumber);
            var dst = string.Concat(cols[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => char.ConvertFromUtf32(int.Parse(h, NumberStyles.HexNumber))));
            Assert.Equal(dst, Confusables.Prototype(src));
            n++;
        }
        Assert.Equal(Confusables.DataEntries, n);
        Assert.Contains(File.ReadLines(path).First(l => l.StartsWith("# Version:")).Substring(10).Trim(), Confusables.DataVersion);
        Assert.Null(Confusables.Prototype('a'));            // a prototype maps to nothing
        Assert.Null(Confusables.Prototype(0x10FFFF));
    }

    [Theory]
    [InlineData("paypal", "pаypal")]       // Cyrillic а
    [InlineData("m", "rn")]                     // the table maps m to rn
    [InlineData("O", "0")]                      // digit zero -> capital O
    [InlineData("l", "1")]                      // digit one, capital I and | -> small l
    [InlineData("l", "I")]
    [InlineData("l", "|")]
    [InlineData("Bob", "Bօb")]             // Armenian օ
    [InlineData("scope", "ѕсоре")] // all Cyrillic: ѕсоре
    public void Skeletons_of_look_alikes_are_equal(string a, string b)
    {
        Assert.Equal(Confusables.Skeleton(a), Confusables.Skeleton(b));
        Assert.True(Confusables.LookAlike(a, b));
    }

    [Fact]
    public void Skeleton_is_exactly_uts39()
    {
        Assert.Equal("rn", Confusables.Skeleton("m"));
        Assert.Equal("O", Confusables.Skeleton("0"));
        Assert.Equal("o", Confusables.Skeleton("o"));                   // case matters to the skeleton: o and O stay apart
        Assert.NotEqual(Confusables.Skeleton("o"), Confusables.Skeleton("0"));
        Assert.Equal("Élan", Confusables.Skeleton("Élan"));   // NFD: an accent is a mark of its own, mapped to nothing
        Assert.Equal("Eʟan", Confusables.Skeleton("Eʟan"));   // small capital L is a prototype of its own in UTS #39
        Assert.Equal("", Confusables.Skeleton(""));
        Assert.Equal("", Confusables.Skeleton(null));
        Assert.Equal(Confusables.Skeleton("x\uD800y"), Confusables.Skeleton("x\uD800y"));  // an unpaired surrogate doesn't throw
    }

    [Theory]
    [InlineData("Elan", "Eʟan")]           // small capital L
    [InlineData("Elan", "ᴇlan")]           // small capital E
    [InlineData("Elan", "ELAN")]
    [InlineData("Elan", "E1an")]
    [InlineData("Elan", "EIan")]
    [InlineData("Elan", "E|an")]
    [InlineData("Elan", "El an")]
    [InlineData("Elan", "E-l.an")]
    [InlineData("Elan", "E_l'an")]
    [InlineData("Elan", "Eian")]                // the old fold's i -> l
    [InlineData("Elan", "Еlan")]           // Cyrillic Е
    [InlineData("Elan", "Εⅼaո")] // Greek Ε, Roman numeral fifty, Armenian ո
    [InlineData("Elan", "𝐄𝐥𝐚𝐧")] // mathematical bold
    [InlineData("Elan", "Ｅlan")]           // fullwidth E
    [InlineData("Rose", "R0s3")]
    [InlineData("Seth", "5eth")]
    [InlineData("Moonlight", "M00nl1ght")]
    [InlineData("Mia", "rnia")]                 // M -> m -> rn
    [InlineData("Bob", "Bօb")]
    [InlineData("paypal", "pаypal")]
    [InlineData("Ela'n", "Elaʼn")]         // modifier letter apostrophe -> ' -> dropped like '
    public void Name_keys_of_look_alikes_are_equal(string a, string b)
    {
        Assert.Equal(Confusables.NameKey(a), Confusables.NameKey(b));
        Assert.True(Confusables.SameName(a, b));
    }

    [Theory]
    [InlineData("Elan", "Elana")]
    [InlineData("Elan", "Élan")]           // accented Latin stays a name of its own: the accent is visible, UTS #39 keeps it
    [InlineData("Elan", "Elán")]
    [InlineData("Zoe", "Zoë")]
    [InlineData("Rose", "Ross")]
    [InlineData("Mia", "Max")]
    [InlineData("Sakura", "さくら")]           // さくら
    [InlineData("さくら", "桜")]           // さくら / 桜
    [InlineData("Νίκη", "Αθηνά")] // Νίκη / Αθηνά
    [InlineData("Мария", "Иван")] // Мария / Иван
    [InlineData("Мария", "Maria")]                    // Мария isn't Latin "Maria": и and я look like nothing Latin
    public void Honest_different_names_keep_different_keys(string a, string b)
    {
        Assert.NotEqual(Confusables.NameKey(a), Confusables.NameKey(b));
        Assert.False(Confusables.SameName(a, b));
    }

    [Theory]
    [InlineData("さくら")]
    [InlineData("桜子")]
    [InlineData("Νίκη")]
    [InlineData("Мария")]
    [InlineData("한글")]               // 한글
    [InlineData("مريم")]   // مريم
    public void Non_latin_names_have_a_key(string name)
    {
        Assert.NotEqual("", Confusables.NameKey(name));
        Assert.Equal(Confusables.NameKey(name), Confusables.NameKey(Confusables.NameKey(name)));  // stable
    }

    [Fact]
    public void Name_key_is_stable_and_safe()
    {
        Assert.Equal("", Confusables.NameKey(""));
        Assert.Equal("", Confusables.NameKey(null));
        Assert.Equal("", Confusables.NameKey(" -_.' "));
        Assert.Equal("elan", Confusables.NameKey("Elan"));
        Assert.Equal("rnla", Confusables.NameKey("Mia"));
        foreach (var s in new[] { "Elan", "Eʟan", "Mia", "M00nl1ght", "x\uD800y", "ﷺ" })
            Assert.Equal(Confusables.NameKey(s), Confusables.NameKey(Confusables.NameKey(s)));
    }
}
