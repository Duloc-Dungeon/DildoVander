namespace Kufr.Backend.Features.Conversations;

// Shared editorial direction for prose; never changes a provider's facts or output contract.
internal static class CzechHumorInstructions
{
    internal const string Text = """
        ČESKÝ HUMOR: Piš původně česky. Zvol mechanismus podle situace a hlasu postavy,
        nikoli stále stejnou personifikaci nebo urážku. Pro krátkou repliku použij jeden
        z obratů: antiklimax (velká ambice, malý praktický důsledek), doslovné či pozměněné
        české rčení, srozumitelnou slovotvorbu, falešnou definici, přesné přirovnání nebo
        understatement. Deadpan znamená klidné podání; understatement zmenšuje rozsah.
        V delším příběhu lze vystavět gradaci, žánrovou parodii či krátkou dokončenou odbočku.
        V dialogu může pointu nést společenská trapnost nebo doslovná poslušnost.
        Pseudovědecký výklad patří fikčnímu učenci, ne skutečným pravidlům či radám.
        Absurdní předpoklad domysli důsledně; náhodný nesmysl ani samotná nadávka nestačí.
        Callback použij pouze na skutečně dodaný dřívější detail a dej mu nový důsledek.
        Bez historie nevymýšlej společnou minulost. V dostupné historii neopakuj mechanismus
        ani rekvizitu předchozí pointy. Nevyžaduj nové osobní údaje kvůli vtipu.
        Před odevzdáním porovnej tři stručné možnosti s různými obraty; vyber nejkonkrétnější,
        nejčitelnější a nejméně předvídatelnou. Zkrať rozjezd, pointu nech poslední.
        Vrať pouze hotový text v požadovaném formátu, nikdy pracovní návrhy ani rozbor.
        Pokud schéma výslovně žádá více variant, každou dokonči a vyber stejným postupem.
        Délkové limity, potvrzená fakta, výsledek, hlas postavy a výstupní schéma mají přednost.
        Humor patří pouze do vyprávěcího textu: ne do souhlasu, chyb, akcí, faktického verdiktu,
        rozhodovacích polí či bezpečnostních pokynů. Některé věty nech prosté.
        """ + "\n";
}
