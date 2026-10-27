using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Kufr.Backend.Features.Game;

internal static class GameTaskCatalog
{
    private sealed record TaskCard(string Title, string Text);
    private static readonly TaskCard[] Classic =
    [
        new("Poražený s noblesou", "Prones patnáctisekundovou oslavnou řeč o soupeřově kouzelnickém výkonu. Přehánění je povoleno."),
        new("Reklamní přestávka", "Zahraj dvacetisekundovou reklamu na svou hůlku. Ani prohra nesmí pokazit prodejní čísla."),
        new("Zkažené zaklínadlo", "Třikrát řekni: Tři třpytivé třísky třídí třesoucí se trpaslík. Klidně vymysli ještě horší zaklínadlo."),
        new("Tichá pomsta", "Pantomimou předveď své poslední kouzlo. Ostatní mohou hádat, ale hodnocení je čistě dobrovolné."),
        new("Uražený kouzelník", "Dvacet sekund hraj mága, který obviňuje vlastní hůlku z administrativní chyby."),
        new("Prokletá lichotka", "Vymysli soupeři přehnaně vznešený kompliment z jeho kouzelnického světa."),
        new("Tanec porážky", "Deset sekund oslavuj soupeře vítězným tancem. Lze tančit i vsedě, jen rukama nebo celou výzvu přeskočit."),
        new("Přípitek na odvetu", "Připij soupeři libovolným nápojem a slavnostně oznam odvetu. Nápoj ani samotný přípitek nejsou podmínkou pokračování."),
    ];
    private static readonly TaskCard[] Adult =
    [
        new("Reklama po půlnoci", "Předveď dvacetisekundovou teleshoppingovou reklamu na svou hůlku. Použij alespoň jeden dvojsmysl a slib, který neprojde reklamačním oddělením."),
        new("Návod pro pokročilé", "Přečti fiktivní návod k hůlce jako svůdný hlas z rádia. Povinná věta: Před použitím zkontrolujte, zda je magie dostatečně vzrušená."),
        new("Zaklínadlo s vedlejšími účinky", "Zahraj mága vysvětlujícího, proč po jeho posledním kouzlu zčervenaly i závěsy. Všechno zůstává u slov a fikce."),
        new("Hůlka roku", "Přednes děkovnou řeč za cenu Nejlépe padnoucí hůlka večera. Porotě vysvětli, že záleží hlavně na technice."),
    ];
    private static readonly TaskCard[] Flirt =
    [
        new("Svůdný pitch", "Vymysli flirtovní seznamovací větu, kterou by tvá postava zkusila v kouzelnickém baru. Můžeš ji říct prázdné židli."),
        new("Pikantní pochvala", "V roli přehnaně galantního mága slož kompliment soupeřově hůlce nebo hernímu stylu. Soupeř nemusí odpovídat."),
        new("Rande s katastrofou", "Zahraj dvacetisekundový konkurz na romantického mága, jehož každá svůdná věta končí reklamací kouzelné techniky."),
    ];
    internal static GamePartyTask Next(string category, int swapCount, string? previous)
    {
        var cards = category switch { "adult" => Adult, "flirt" => Flirt, _ => Classic };
        var index = (int)((uint)swapCount % (uint)cards.Length);
        if (cards[index].Text == previous) { index = (index + 1) % cards.Length; }
        var card = cards[index];
        return new(card.Title, card.Text, category, "pending", swapCount);
    }

    internal static GamePartyTask NextEligible(bool adultEnabled, bool flirtEnabled, string stableSeed)
    {
        var categories = new List<string> { "classic" };
        if (adultEnabled) { categories.Add("adult"); }
        if (flirtEnabled) { categories.Add("flirt"); }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(stableSeed));
        var categoryIndex = (int)(BinaryPrimitives.ReadUInt32LittleEndian(hash) % (uint)categories.Count);
        var cardIndex = (int)(BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(4)) % int.MaxValue);
        return Next(categories[categoryIndex], cardIndex, null) with { ExchangeCount = 0 };
    }
}
