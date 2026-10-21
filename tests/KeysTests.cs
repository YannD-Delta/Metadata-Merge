using MetadataMerge.Core;

public static class KeysTests
{
    public static void NameIgnoresCaseAndSpacing()
    {
        Assert.Equal(Keys.Name("Open  World", true), Keys.Name(" open world ", true));
        Assert.Equal(Keys.Name("Open World", false), Keys.Name("OPEN   world", false));
    }

    public static void LooseNameIgnoresPunctuationAndAccents()
    {
        Assert.Equal(Keys.Name("Co-op", true), Keys.Name("Co-Op", true));
        Assert.Equal(Keys.Name("Coop", true), Keys.Name("Co-op", true));
        Assert.Equal(Keys.Name("Single-player", true), Keys.Name("Singleplayer", true));
        Assert.Equal(Keys.Name("Pokémon", true), Keys.Name("Pokemon", true));
        Assert.Equal(Keys.Name("Hack & Slash", true), Keys.Name("Hack and Slash", true));
    }

    public static void StrictNameKeepsPunctuation()
    {
        Assert.True(Keys.Name("Co-op", false) != Keys.Name("Coop", false));
    }

    public static void NameOfOnlySymbolsFallsBackToTheStrictKey()
    {
        Assert.Equal("!!!", Keys.Name("!!!", true));
        Assert.Equal("", Keys.Name("   ", true));
        Assert.Equal("", Keys.Name(null, true));
    }

    public static void CompanyIgnoresLegalForms()
    {
        Assert.Equal(Keys.Company("Valve", true), Keys.Company("Valve Corporation", true));
        Assert.Equal(Keys.Company("Frictional Games", true), Keys.Company("Frictional Games AB", true));
        Assert.Equal(Keys.Company("CD Projekt", true), Keys.Company("CD Projekt S.A.", true));
        Assert.Equal(Keys.Company("Bethesda Softworks", true), Keys.Company("Bethesda Softworks LLC", true));
        Assert.Equal(Keys.Company("Remedy Entertainment", true), Keys.Company("Remedy Entertainment Oy", true));
    }

    public static void CompanyNeverStripsTheOnlyWord()
    {
        Assert.Equal("co", Keys.Company("Co", true));
        Assert.Equal("inc", Keys.Company("Inc.", true));
    }

    public static void CompanyStrictModeIsTheNameKey()
    {
        Assert.Equal(Keys.Name("Valve Corporation", false), Keys.Company("Valve Corporation", false));
        Assert.True(Keys.Company("Valve", false) != Keys.Company("Valve Corporation", false));
    }

    public static void LinkKeyIgnoresSchemeWwwSlashAndFragment()
    {
        Assert.Equal(Keys.Link("https://www.Example.com/Game/"), Keys.Link("http://example.com/game#top"));
        Assert.True(Keys.Link("https://example.com/a") != Keys.Link("https://example.com/b"));
        Assert.True(Keys.Link("https://example.com/?id=1") != Keys.Link("https://example.com/?id=2"));
        Assert.Equal("", Keys.Link(" "));
    }
}

public static class SynonymsTests
{
    public static void RenamesAnAliasKeepsTheRest()
    {
        var s = new Synonyms("Role-playing (RPG)=RPG\nShooter=Shooter", true, false);
        Assert.Equal("RPG", s.Apply("Role-Playing (RPG)", Keys.Name("Role-Playing (RPG)", true)));
        Assert.Equal("Puzzle", s.Apply("Puzzle", Keys.Name("Puzzle", true)));
        Assert.Equal(2, s.Count);
    }

    public static void EmptyRightSideDropsTheValue()
    {
        var s = new Synonyms("Early Access=", true, false);
        Assert.True(s.Apply("early access", Keys.Name("early access", true)) == null);
    }

    public static void CommentsBlankAndBrokenLinesAreIgnored()
    {
        var s = new Synonyms("# a comment\n\nnoequalsign\n=nothing\r\nFPS=Shooter\r\n", true, false);
        Assert.Equal(1, s.Count);
        Assert.Equal("Shooter", s.Apply("FPS", Keys.Name("FPS", true)));
    }

    public static void CompanyRulesUseTheCompanyKey()
    {
        var s = new Synonyms("Valve Software=Valve", true, true);
        Assert.Equal("Valve", s.Apply("Valve Software Inc", Keys.Company("Valve Software Inc", true)));
    }
}
