using System.Security.Cryptography;
using System.Text;

namespace Kufr.Backend.Features.Show;

internal static class DuelPartyTasks
{
    private static readonly DuelPartyTask[] Tasks =
    [
        new("toast-sip-v1", "Přípitek na účet komise",
            "Jestli chceš, připij vítězné postavě malým douškem nápoje, který sis sám vybral. Alkohol není potřeba.",
            "Zvedni prázdnou sklenici nebo jen slavnostně zamávej.", "Výbor pro sraby tvou žádost schválil. Statečnost je v údržbě. Olivander se směje také; soudě podle polohy už jedná s podlahou.", true),
        new("finish-drink-v1", "Poslední kapitola sklenice",
            "Pokud se ti to hodí, dopij svůj stávající nápoj vlastním tempem. Vítězná postava ti může připravit nápoj, který si sám zvolíš.",
            "Nech sklenici být a vyber si vítězný přípitek beze spěchu.", "Výbor pro sraby tvou žádost schválil. Statečnost je v údržbě. Olivander se směje také; soudě podle polohy už jedná s podlahou.", true),
        new("dramatic-count-v1", "Olympiáda formulářů",
            "Vyber si deset kliků, nebo pohodlné kroužení rameny; vítěz může počítat s přehnaně dramatickou intonací.",
            "Předveď jedno imaginární opakování vsedě, nebo úkol přeskoč.", "Výbor pro sraby tvou žádost schválil. Statečnost je v údržbě. Olivander se směje také; soudě podle polohy už jedná s podlahou.", true),
        new("thirty-second-toast-v1", "Půlminutová řeč vítěze",
            "Prones třicetisekundový přípitek vítězné postavě, jako by právě objevila daňově uznatelnou jiskru.",
            "Stačí jedna slavnostní věta; úplné přeskočení je rovněž povolené.", "Výbor pro sraby tvou žádost schválil. Statečnost je v údržbě. Olivander se směje také; soudě podle polohy už jedná s podlahou.", true),
        new("press-conference-v1", "Tisková konference poražené důstojnosti",
            "Odpověz na jednu otázku neviditelné komise: kde se podle tebe ztratilo vítězství? Odpověď smí být naprosto absurdní.",
            "Můžeš pouze říct: ‚Bez komentáře, formulář se odvolal.‘",
            "Výbor pro sraby tvou žádost schválil. Statečnost je v údržbě. Olivander se směje také; soudě podle polohy už jedná s podlahou.", false),
        new("wand-household-ad-v1", "Reklama na vítězovu hůlku",
            "Vyretušuj vítězovu hůlku slovně na mimořádně nepraktickou domácí pomůcku.",
            "Pochval jednu její smyšlenou kancelářskou vlastnost, nebo přeskoč.",
            "Výbor pro sraby tvou žádost schválil. Statečnost je v údržbě. Olivander se směje také; soudě podle polohy už jedná s podlahou.", true),
        new("seated-dance-v1", "Dvorní tanec v úsporném režimu",
            "Zatanči patnáct vteřin vsedě; vítězná postava může dělat dirigenta s vážností operního ministerstva.",
            "Předveď jediné gesto rukou nebo si vyber úplné přeskočení.",
            "Výbor pro sraby tvou žádost schválil. Statečnost je v údržbě. Olivander se směje také; soudě podle polohy už jedná s podlahou.", true),
        new("royal-announcement-v1", "Královské oznámení",
            "Oznam porážku jako královský herold, který právě zjistil, že koruna potřebuje potvrzení ve třech kopiích.",
            "Vydej jen jedno vznešené ‚ach‘, nebo přeskoč.",
            "Výbor pro sraby tvou žádost schválil. Statečnost je v údržbě. Olivander se směje také; soudě podle polohy už jedná s podlahou.", false),
        new("comic-statue-v1", "Socha vítězného okamžiku",
            "Zaujmi na pět vteřin nehybnou sochařskou pózu poražené postavy, která čeká na odvolací formulář.",
            "Popiš pózu slovy, aniž by ses musel zvedat; přeskočení je v řádu.",
            "Výbor pro sraby tvou žádost schválil. Statečnost je v údržbě. Olivander se směje také; soudě podle polohy už jedná s podlahou.", false),
        new("fictional-weather-v1", "Předpověď počasí pro kouzelný svět",
            "Předpověz počasí v tomto čistě smyšleném světě a připiš vítězství drobnému atmosférickému jevu.",
            "Stačí jediná věta o mlze, která podala žádost o dovolenou.",
            "Výbor pro sraby tvou žádost schválil. Statečnost je v údržbě. Olivander se směje také; soudě podle polohy už jedná s podlahou.", false),
        new("paperwork-mime-v1", "Pantomima ztracené žádosti",
            "Beze slov ukaž, jak vítězova žádost o slávu oběhla tři kanceláře a vrátila se jako obálka.",
            "Popiš pantomimu vsedě jedním zvukem, nebo ji předej archivu.",
            "Výbor pro sraby tvou žádost schválil. Statečnost je v údržbě. Olivander se směje také; soudě podle polohy už jedná s podlahou.", true),
        new("wand-regulation-v1", "Nová vyhláška o vítězově hůlce",
            "Vymysli jednu naprosto zbytečnou vyhlášku, kterou si vítězova hůlka musí odteď sama vyřizovat.",
            "Stačí název vyhlášky; komise přijímá i mlčení.",
            "Výbor pro sraby tvou žádost schválil. Statečnost je v údržbě. Olivander se směje také; soudě podle polohy už jedná s podlahou.", true)
    ];

    internal static DuelPartyTask? ForResult(string seed, DuelResult? result)
    {
        if (result is null || result.Winner == "draw") { return null; }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed + "\u001fduel-party-task-catalog-v1"));
        return Tasks[hash[0] % Tasks.Length];
    }

    internal static bool IsCatalogued(DuelPartyTask? task) => task is null || Tasks.Any(item => item == task);
}
