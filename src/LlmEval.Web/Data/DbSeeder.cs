using LlmEval.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace LlmEval.Web.Data;

public static class DbSeeder
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        var factory = services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("DbSeeder");

        // Postgres in a fresh container may still be warming up even after Aspire's WaitFor.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync();
                await db.Database.MigrateAsync();
                break;
            }
            catch (Exception ex) when (attempt < 10)
            {
                log.LogWarning("Migration attempt {Attempt} failed: {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        await using var ctx = await factory.CreateDbContextAsync();
        if (await ctx.Users.AnyAsync()) return;

        log.LogInformation("Seeding database");
        var user = new User { Name = "admin", AvatarHue = EvalService.HueFor("admin") };
        ctx.Users.Add(user);
        ctx.Providers.AddRange(SeedProviders());
        ctx.TestCases.AddRange(SeedTestCases(user));
        await ctx.SaveChangesAsync();
    }

    private static IEnumerable<Provider> SeedProviders()
    {
        static LlmModel M(string id, string name, double? temp = null, bool judge = false) => new() { ModelId = id, DisplayName = name, Temperature = temp, IsJudge = judge };

        yield return new Provider
        {
            Name = "Claude Code CLI",
            Type = ProviderType.ClaudeCli,
            TimeoutSeconds = 300,
            Models = [M("sonnet", "Claude Sonnet (CLI)", judge: true), M("haiku", "Claude Haiku (CLI)"), M("opus", "Claude Opus (CLI)")]
        };
        yield return new Provider
        {
            Name = "Ollama (lokalnie)",
            Type = ProviderType.Ollama,
            BaseUrl = "http://localhost:11434",
            TimeoutSeconds = 600,
            Models = [M("llama3.2", "Llama 3.2"), M("gemma3", "Gemma 3"), M("qwen3", "Qwen 3")]
        };
        yield return new Provider
        {
            Name = "Anthropic API",
            Type = ProviderType.Anthropic,
            Enabled = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")),
            Models =
            [
                M("claude-opus-5-5", "Claude Opus 5.5", judge: true),
                M("claude-sonnet-5-5", "Claude Sonnet 5.5"),
                M("claude-haiku-4-5-20251001", "Claude Haiku 4.5")
            ]
        };
        yield return new Provider
        {
            Name = "OpenAI API",
            Type = ProviderType.OpenAI,
            Enabled = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_API_KEY")),
            Models = [M("gpt-5", "GPT-5"), M("gpt-5-mini", "GPT-5 mini"), M("gpt-4.1", "GPT-4.1", 0.2)]
        };
        yield return new Provider
        {
            Name = "Codex CLI",
            Type = ProviderType.CodexCli,
            Enabled = false,
            TimeoutSeconds = 300,
            Models = [M("default", "Codex (domyślny model)")]
        };
    }

    private static IEnumerable<TestCase> SeedTestCases(User user)
    {
        TestCase T(string title, string prompt, string? data, string[] tags, string? system = null) => new()
        {
            Title = title, Prompt = prompt, Data = data, Tags = [.. tags], SystemPrompt = system, CreatedById = user.Id
        };

        yield return T("Streszczenie spotkania – sprint planning",
            "Streść poniższe spotkanie w maksymalnie 8 punktach. Na końcu wypisz osobno: podjęte decyzje, otwarte kwestie i ryzyka.",
            """
            [09:02] Marta (PO): Dobra, zaczynamy planning sprintu 24. Cel sprintu: wypuścić płatności BLIK w aplikacji mobilnej.
            [09:03] Tomek (backend): Integracja z bramką jest gotowa w 70%. Brakuje obsługi webhooków o zmianie statusu i retry.
            [09:04] Marta: Ile to zajmie?
            [09:04] Tomek: Webhooki 3 dni, retry z idempotencją kolejne 2. Do tego testy na sandboxie bramki, a ten sandbox lubi leżeć.
            [09:06] Ola (mobile): Po naszej stronie ekran płatności jest gotowy, ale design się zmienił w piątek. Nowy flow ma dodatkowy ekran potwierdzenia kodu.
            [09:07] Kuba (UX): Tak, testy z użytkownikami wykazały, że 4 na 10 osób nie wiedziało, czy płatność przeszła. Ekran potwierdzenia jest konieczny.
            [09:08] Ola: To jest +3 dni dla iOS i Androida każdy, chyba że zrobimy to we Flutterze wspólnie, wtedy 3 dni razem.
            [09:09] Marta: Robimy wspólnie. Co z QA?
            [09:10] Ania (QA): Potrzebuję co najmniej 3 dni na regresję płatności, w tym kartowych, bo dotykamy wspólnego modułu.
            [09:12] Tomek: Jeszcze jedno – certyfikat do bramki wygasa 15-go. Ktoś musi go odnowić, bo inaczej produkcja padnie.
            [09:12] Marta: Kto ma dostęp do panelu bramki?
            [09:13] Tomek: Tylko Rafał, a on jest na urlopie do 14-go.
            [09:13] Marta: Słabo. Napiszę dziś do Rafała, żeby przekazał dostęp komuś z zespołu.
            [09:15] Ania: Czy BLIK ma działać też dla zwrotów?
            [09:15] Marta: Nie, zwroty w kolejnym sprincie. W tym tylko płatność.
            [09:16] Kuba: Proponuję, żebyśmy dodali analitykę na nowym ekranie, żeby zmierzyć porzucenia.
            [09:17] Marta: Ok, ale jako nice-to-have, jeśli starczy czasu.
            [09:18] Tomek: Mamy 10 dni roboczych. Backend 5, mobile 3 + integracja 1, QA 3. Na styk.
            [09:19] Marta: Zgoda, zaczynamy. Daily o 9:30 jak zwykle.
            """,
            ["streszczenie", "spotkanie"]);

        yield return T("Action items z retrospektywy (JSON)",
            """
            Z poniższej transkrypcji retrospektywy wyciągnij wszystkie zadania do wykonania.
            Zwróć WYŁĄCZNIE poprawny JSON w formacie:
            [{"zadanie": string, "osoba": string | null, "termin": string | null, "priorytet": "wysoki" | "średni" | "niski"}]
            Nie dodawaj zadań, których nie było w rozmowie.
            """,
            """
            Kasia: Co poszło źle? Deploy w czwartek wywalił się trzy razy przez migracje.
            Michał: Bo migracje nie są testowane na kopii produkcji. Mogę do końca przyszłego tygodnia postawić job w CI, który odpala migracje na zanonimizowanym dumpie.
            Kasia: Super, bierz. To jest dla nas najważniejsze.
            Bartek: Druga rzecz – code review trwa średnio dwa dni. PR-y wiszą.
            Kasia: Propozycje?
            Bartek: Rotacyjny dyżurny od review, codziennie ktoś inny. Mogę rozpisać grafik.
            Kasia: Ok, Bartek rozpisuje do poniedziałku.
            Ewa: Dokumentacja API jest nieaktualna, klienci piszą do supportu. Ktoś powinien się tym zająć, ale nie wiem kto.
            Kasia: Zostawmy na razie, wrócimy do tego za tydzień.
            Michał: A, i trzeba odnowić licencję na Rider dla nowych osób, kończy się w marcu.
            Kasia: Zgłoszę to do biura jeszcze dziś.
            Ewa: Fajnie było, że pair programming przy refaktorze koszyka działał. Kontynuujmy.
            """,
            ["ekstrakcja", "json", "spotkanie"]);

        yield return T("Odpowiedź na reklamację klienta",
            "Napisz odpowiedź na poniższą reklamację w imieniu działu obsługi klienta sklepu ElektroMax. Ton: empatyczny, konkretny, bez korporacyjnej waty. Zaproponuj realne rozwiązanie. Max 180 słów.",
            """
            Temat: SKANDAL!!! zamówienie #48213

            Zamówiłem odkurzacz 3 tygodnie temu, zapłaciłem od razu przelewem. Na stronie było "wysyłka 24h". Po tygodniu dostałem maila, że "produkt chwilowo niedostępny". Dzwoniłem dwa razy, raz się rozłączyło po 20 minutach czekania, za drugim razem pani powiedziała, że "nic nie wie". Wczoraj przyszła paczka – z ŻELAZKIEM. Nie zamawiałem żelazka.
            Chcę zwrotu pieniędzy natychmiast i nie zamierzam płacić za odesłanie tego żelaza. Jeśli do piątku nie dostanę odpowiedzi, opisuję wszystko na Facebooku i zgłaszam do UOKiK.

            Krzysztof Nowicki
            """,
            ["obsługa klienta", "pisanie"]);

        yield return T("Code review – C# serwis zamówień",
            "Zrób code review poniższego kodu. Wypisz błędy w kolejności od najpoważniejszego, każdy z krótkim uzasadnieniem i poprawką. Nie komentuj stylu, jeśli nie wpływa na poprawność.",
            """
            public class OrderService
            {
                private static List<Order> _cache = new();
                private readonly AppDbContext _db;
                public OrderService(AppDbContext db) => _db = db;

                public async Task<decimal> GetTotal(int orderId)
                {
                    var order = _cache.FirstOrDefault(o => o.Id == orderId);
                    if (order == null)
                    {
                        order = _db.Orders.Include(o => o.Lines).First(o => o.Id == orderId);
                        _cache.Add(order);
                    }
                    decimal total = 0;
                    foreach (var line in order.Lines)
                        total += line.Price * line.Quantity;
                    if (order.DiscountPercent > 0)
                        total = total - total * order.DiscountPercent / 100;
                    return Math.Round(total);
                }

                public async void MarkPaid(int orderId)
                {
                    var order = await _db.Orders.FindAsync(orderId);
                    order.Status = "Paid";
                    order.PaidAt = DateTime.Now;
                    _db.SaveChanges();
                }

                public List<Order> Search(string customer)
                {
                    return _db.Orders.FromSqlRaw($"SELECT * FROM Orders WHERE Customer LIKE '%{customer}%'").ToList();
                }
            }
            """,
            ["kod", "code review", "c#"]);

        yield return T("Diagnoza wyjątku z logów",
            "Wyjaśnij, co jest przyczyną poniższego błędu i jak go naprawić. Podaj najbardziej prawdopodobną przyczynę jako pierwszą i ewentualne alternatywy.",
            """
            System.InvalidOperationException: A second operation was started on this context instance before a previous operation completed. This is usually caused by different threads concurrently using the same instance of DbContext.
               at Microsoft.EntityFrameworkCore.Infrastructure.Internal.ConcurrencyDetector.EnterCriticalSection()
               at Microsoft.EntityFrameworkCore.Query.Internal.SingleQueryingEnumerable`1.AsyncEnumerator.MoveNextAsync()
               at Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync[TSource](IQueryable`1 source, CancellationToken cancellationToken)
               at Shop.Web.Components.Pages.Dashboard.LoadOrdersAsync() in Dashboard.razor:line 88
               at Shop.Web.Components.Pages.Dashboard.OnInitializedAsync() in Dashboard.razor:line 61

            Kontekst: Blazor Server, .NET 9. Dashboard.razor:
            @inject AppDbContext Db
            protected override async Task OnInitializedAsync()
            {
                var t1 = LoadOrdersAsync();
                var t2 = LoadCustomersAsync();
                var t3 = LoadStatsAsync();
                await Task.WhenAll(t1, t2, t3);
            }
            """,
            ["kod", "debugging", "c#"]);

        yield return T("SQL z pytania biznesowego",
            "Napisz zapytanie PostgreSQL odpowiadające na pytanie: „Którzy 5 klienci wydali najwięcej w 2025 roku, licząc tylko opłacone zamówienia, i ile wynosiła ich średnia wartość zamówienia?” Wyjaśnij krótko kluczowe decyzje.",
            """
            CREATE TABLE customers (id serial PRIMARY KEY, name text NOT NULL, country char(2));
            CREATE TABLE orders (
              id serial PRIMARY KEY,
              customer_id int REFERENCES customers(id),
              created_at timestamptz NOT NULL,
              status text NOT NULL -- 'new' | 'paid' | 'cancelled' | 'refunded'
            );
            CREATE TABLE order_lines (
              id serial PRIMARY KEY,
              order_id int REFERENCES orders(id),
              unit_price numeric(10,2) NOT NULL,
              quantity int NOT NULL,
              discount numeric(4,3) NOT NULL DEFAULT 0 -- 0.150 = 15%
            );
            """,
            ["kod", "sql"]);

        yield return T("Ekstrakcja danych z faktury",
            """
            Wyciągnij dane z faktury do JSON zgodnego ze schematem:
            {"numer": string, "data_wystawienia": "YYYY-MM-DD", "sprzedawca": {"nazwa": string, "nip": string}, "nabywca": {"nazwa": string, "nip": string}, "pozycje": [{"nazwa": string, "ilosc": number, "cena_netto": number, "vat_proc": number}], "suma_netto": number, "suma_brutto": number}
            Jeśli jakaś suma w dokumencie się nie zgadza z pozycjami, dodaj pole "uwagi" z opisem rozbieżności. Zwróć tylko JSON.
            """,
            """
            FAKTURA VAT nr FV/2025/10/117
            Data wystawienia: 3 października 2025      Data sprzedaży: 30.09.2025
            Sprzedawca: Softwarex Sp. z o.o., ul. Długa 5, 50-001 Wrocław, NIP 897-17-23-456
            Nabywca: Piekarnia "Złoty Kłos" Jan Kowal, Rynek 2, 55-100 Trzebnica, NIP 915 155 22 11

            Lp | Nazwa                               | Ilość | Cena netto | VAT
            1  | Licencja POS – 12 mies.             | 2     | 1 200,00   | 23%
            2  | Wdrożenie i szkolenie (godz.)       | 6     | 180,00     | 23%
            3  | Drukarka fiskalna Posnet Thermal    | 1     | 1 850,00   | 23%

            Razem netto: 5 330,00 zł
            VAT 23%: 1 225,90 zł
            Do zapłaty: 6 555,90 zł
            Termin płatności: 14 dni, przelew
            """,
            ["ekstrakcja", "json"]);

        yield return T("Klasyfikacja zgłoszeń supportu",
            """
            Sklasyfikuj każde zgłoszenie do jednej kategorii: BŁĄD, PYTANIE, PROŚBA_O_FUNKCJĘ, PŁATNOŚCI, KONTO, SPAM.
            Dodaj pilność: P1 (blokuje pracę wielu osób), P2 (blokuje jedną osobę), P3 (reszta).
            Format odpowiedzi: tabela markdown z kolumnami: #, kategoria, pilność, uzasadnienie (max 10 słów).
            """,
            """
            1. Od rana nikt w firmie nie może się zalogować, wywala błąd 502.
            2. Czy da się wyeksportować raport do Excela?
            3. Pobraliście mi dwa razy opłatę za październik!!!
            4. Fajnie by było, gdyby był tryb ciemny.
            5. Zmieniłam nazwisko po ślubie, jak zaktualizować dane na koncie?
            6. Przycisk "Zapisz" w formularzu faktury nic nie robi, nie mogę wystawić faktury klientowi który czeka.
            7. CONGRATULATIONS!!! You won iPhone 17, click here to claim
            8. Po aktualizacji wykresy na dashboardzie pokazują dane sprzed tygodnia, ale da się pracować.
            """,
            ["klasyfikacja"]);

        yield return T("Tłumaczenie fragmentu umowy PL → EN",
            "Przetłumacz poniższy fragment umowy na angielski (British English), zachowując rejestr prawniczy i numerację. Nie tłumacz nazw własnych.",
            """
            § 4. Odpowiedzialność
            1. Wykonawca ponosi odpowiedzialność za szkody wyrządzone Zamawiającemu wyłącznie w przypadku winy umyślnej lub rażącego niedbalstwa.
            2. Łączna odpowiedzialność Wykonawcy z tytułu niniejszej Umowy, niezależnie od podstawy prawnej, ograniczona jest do wysokości wynagrodzenia netto wypłaconego w okresie 12 miesięcy poprzedzających zdarzenie wywołujące szkodę.
            3. Wykonawca nie ponosi odpowiedzialności za utracone korzyści Zamawiającego.
            4. Ograniczenia, o których mowa w ust. 1–3, nie mają zastosowania do szkód wyrządzonych naruszeniem obowiązków określonych w § 7 (Poufność).
            """,
            ["tłumaczenie", "prawo"]);

        yield return T("User stories z opisu wymagań",
            "Na podstawie notatki od klienta przygotuj user stories w formacie „Jako… chcę… aby…” z kryteriami akceptacji (Given/When/Then). Wypisz też pytania do klienta tam, gdzie wymagania są niejasne.",
            """
            Notatka z rozmowy z klientem (sieć siłowni FitBox):
            - klienci mają rezerwować zajęcia grupowe przez aplikację, limit miejsc na zajęciach
            - jak ktoś nie przyjdzie 3 razy w miesiącu to blokada rezerwacji na tydzień
            - lista rezerwowa – jak się zwolni miejsce to pierwsza osoba z listy dostaje powiadomienie i ma chwilę na potwierdzenie
            - instruktor widzi listę zapisanych i odhacza obecność
            - karnet open lub na wejścia, przy wejściowym rezerwacja zjada wejście (chyba że odwoła odpowiednio wcześniej)
            """,
            ["analiza", "wymagania"]);

        yield return T("Zadanie logiczne – harmonogram",
            "Rozwiąż zadanie. Pokaż tok rozumowania krok po kroku, a na końcu podaj jednoznaczną odpowiedź.",
            """
            Pięć osób – Ada, Bartek, Celina, Darek i Ewa – ma prezentacje w jeden dzień, każda w innym slocie: 9:00, 10:00, 11:00, 12:00, 13:00.
            - Bartek występuje później niż Ewa, ale wcześniej niż Celina.
            - Darek nie występuje ani pierwszy, ani ostatni.
            - Ada występuje bezpośrednio po Darku.
            - Celina nie występuje o 13:00.
            O której godzinie występuje każda z osób? Czy rozwiązanie jest jednoznaczne?
            """,
            ["rozumowanie"]);

        yield return T("Release notes z listy commitów",
            "Na podstawie listy commitów napisz release notes dla użytkowników końcowych (nie programistów). Pogrupuj na: Nowości, Poprawki, Inne. Pomiń zmiany czysto techniczne, które nie wpływają na użytkownika.",
            """
            a1f3c2e feat(invoices): bulk export to PDF (#812)
            9bd2e10 fix(auth): session expired too early on mobile Safari
            77c01aa chore(deps): bump Npgsql 9.0.3 -> 9.0.4
            e22fa01 feat(dashboard): dark mode toggle
            5c9e8f3 refactor(core): extract PriceCalculator
            0a13bd7 fix(invoices): VAT rounding off by 0.01 for multi-line invoices
            d4e5f6a ci: cache nuget packages
            3f3f3f1 feat(notifications): email digest daily/weekly setting
            8e8e8e2 fix(search): polish diacritics ignored in customer search
            1b2b3b4 test: add integration tests for invoice export
            """,
            ["pisanie", "produkt"]);

        yield return T("Prosty język – pismo urzędowe",
            "Przepisz poniższy tekst prostym językiem, tak żeby zrozumiała go osoba bez wykształcenia prawniczego. Zachowaj wszystkie istotne terminy i kwoty. Na końcu dodaj sekcję „Co muszę zrobić?” w punktach.",
            """
            Na podstawie art. 21 ust. 1 ustawy z dnia 13 września 1996 r. o utrzymaniu czystości i porządku w gminach, w związku z uchwałą Rady Miejskiej nr XII/145/2025, uprzejmie zawiadamia się, iż z dniem 1 stycznia 2026 r. stawka opłaty za gospodarowanie odpadami komunalnymi ulega zmianie i wynosi 38,00 zł miesięcznie od osoby zamieszkującej nieruchomość. Właściciele nieruchomości kompostujący bioodpady w kompostowniku przydomowym uprawnieni są do zwolnienia w części z opłaty w wysokości 4,00 zł od osoby, pod warunkiem złożenia stosownej deklaracji w terminie 14 dni od dnia powstania zmiany. Niezłożenie deklaracji skutkuje naliczeniem opłaty w wysokości pełnej. Opłatę uiszcza się bez wezwania w terminie do 15. dnia każdego miesiąca za dany miesiąc.
            """,
            ["pisanie", "upraszczanie"]);

        yield return T("Analiza sentymentu recenzji aplikacji",
            "Przeanalizuj recenzje aplikacji. Dla każdej podaj sentyment (pozytywny/neutralny/negatywny/mieszany) i główny temat. Potem podsumuj 3 najważniejsze problemy, które zespół powinien naprawić najpierw, z uzasadnieniem.",
            """
            ★★★★★ "Najlepsza apka do budżetu, w końcu widzę gdzie uciekają pieniądze."
            ★☆☆☆☆ "Po ostatniej aktualizacji nie synchronizuje się z mBankiem. Bez tego jest bezużyteczna."
            ★★★☆☆ "Fajna, ale reklamy co 30 sekund to przesada. Zapłaciłbym za wersję bez reklam."
            ★★☆☆☆ "Synchronizacja z bankiem działa raz na trzy razy. Wykresy ładne."
            ★★★★☆ "Super, tylko brakuje eksportu do CSV."
            ★☆☆☆☆ "Pobrali mi subskrypcję mimo że anulowałem. Support milczy od tygodnia."
            ★★★★☆ "Działa ok, ale dark mode by się przydał bo w nocy razi."
            ★★☆☆☆ "Znowu nie widzi transakcji z PKO. Ile można."
            """,
            ["klasyfikacja", "analiza"]);

        yield return T("Testy jednostkowe xUnit",
            "Napisz testy jednostkowe xUnit dla poniższej metody. Pokryj przypadki brzegowe. Jeśli zauważysz błąd w implementacji, napisz test, który go wykrywa, i opisz błąd.",
            """
            public static class PeselValidator
            {
                public static bool IsValid(string pesel)
                {
                    if (pesel.Length != 11) return false;
                    int[] weights = { 1, 3, 7, 9, 1, 3, 7, 9, 1, 3 };
                    int sum = 0;
                    for (int i = 0; i < 10; i++)
                        sum += (pesel[i] - '0') * weights[i];
                    int control = (10 - sum % 10) % 10;
                    return control == pesel[10] - '0';
                }
            }
            """,
            ["kod", "testy", "c#"]);

        yield return T("Odpowiedź tylko z dokumentu (halucynacje)",
            """
            Odpowiedz na pytania klienta WYŁĄCZNIE na podstawie regulaminu poniżej. Jeśli odpowiedzi nie ma w regulaminie, napisz wprost „Regulamin tego nie określa”. Przy każdej odpowiedzi podaj numer paragrafu.

            Pytania:
            1. Ile mam dni na zwrot towaru kupionego online?
            2. Czy mogę zwrócić towar kupiony w sklepie stacjonarnym?
            3. Kto płaci za przesyłkę zwrotną?
            4. Czy mogę zwrócić rozpakowane słuchawki?
            5. Czy dostanę zwrot na kartę podarunkową?

            {{data}}
            """,
            """
            REGULAMIN SKLEPU INTERNETOWEGO DOMTECH
            § 1. Konsument może odstąpić od umowy zawartej na odległość w terminie 30 dni od dnia otrzymania towaru, bez podania przyczyny.
            § 2. Koszt przesyłki zwrotnej ponosi Sklep, jeżeli Konsument skorzysta z etykiety zwrotnej wygenerowanej w panelu klienta. W pozostałych przypadkach koszt ponosi Konsument.
            § 3. Zwrot płatności następuje tą samą metodą, której użył Konsument, w terminie 14 dni od otrzymania zwracanego towaru.
            § 4. Prawo odstąpienia nie przysługuje w odniesieniu do towarów dostarczanych w zapieczętowanym opakowaniu, których po otwarciu nie można zwrócić ze względu na ochronę zdrowia lub ze względów higienicznych, jeżeli opakowanie zostało otwarte po dostarczeniu.
            § 5. Reklamacje rozpatrywane są w terminie 14 dni.
            """,
            ["rag", "halucynacje"]);
    }
}
