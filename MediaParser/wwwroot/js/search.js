(() => {
    "use strict";
    const statuses = {
        found: { label: "Знайдено", order: 0 },
        checking: { label: "Перевіряється", order: 1 },
        pending: { label: "Очікує", order: 2 },
        unconfirmed: { label: "Не підтверджено", order: 3 },
        blocked: { label: "Доступ обмежено", order: 4 },
        error: { label: "Помилка", order: 5 },
        cancelled: { label: "Зупинено", order: 6 },
        notfound: { label: "Не знайдено", order: 7 },
        legacy: { label: "Попередній результат", order: 8 }
    };
    const terminalStates = new Set(["completed", "cancelled", "failed"]);
    const collator = new Intl.Collator("uk", { sensitivity: "base", numeric: true });
    const matchesStatus = (status, filter) => filter === "all" || status === filter
        || (filter === "uncertain" && ["blocked", "unconfirmed"].includes(status))
        || (filter === "awaiting" && ["pending", "checking"].includes(status))
        || (filter === "failed" && ["error", "cancelled"].includes(status));
    const normalizeText = value => String(value ?? "").toLocaleLowerCase("uk");
    const safeUrl = value => {
        try {
            const url = new URL(value);
            return ["https:", "http:"].includes(url.protocol) ? url.href : null;
        } catch { return null; }
    };
    const declarationProviders = [
        { key: "nazk", origin: "https://public.nazk.gov.ua", searchPath: "/documents/list", title: "Публічні декларації НАЗК", documentLabel: "Відкрити декларацію НАЗК", searchLabel: "Усі результати в офіційному реєстрі", note: "Джерело: офіційний публічний реєстр НАЗК.", documentPath: id => `/documents/${id}` },
        { key: "youcontrol", origin: "https://youcontrol.com.ua", searchPath: "/catalog/individuals/search/", title: "Декларації YouControl", documentLabel: "Відкрити на YouControl", searchLabel: "Шукати декларації на YouControl", note: "Метадані надані YouControl — окремим сервісом, а не офіційним реєстром НАЗК.", documentPath: id => `/catalog/individuals/declaration/${id}/` }
    ];
    const declarationProvider = value => {
        try {
            const url = new URL(value);
            if (url.username || url.password || url.hash) return null;
            return declarationProviders.find(provider => url.origin === provider.origin && (url.pathname === provider.searchPath
                || provider.key === "youcontrol" && !url.search && /^\/catalog\/individuals\/[a-z0-9]+(?:-[a-z0-9]+)+\/$/.test(url.pathname))) || null;
        } catch { return null; }
    };
    const declarationDocumentUrl = (value, id, provider) => {
        if (!provider || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(String(id || ""))) return null;
        try {
            const url = new URL(value);
            return url.origin === provider.origin && !url.username && !url.password && !url.search && !url.hash
                && url.pathname.toLowerCase() === provider.documentPath(String(id).toLowerCase()) ? url.href : null;
        } catch { return null; }
    };
    const formatDate = value => {
        const date = new Date(value);
        return value && !Number.isNaN(date.getTime()) ? date.toLocaleString("uk-UA", {
            day: "2-digit", month: "2-digit", year: "numeric", hour: "2-digit", minute: "2-digit", second: "2-digit"
        }) : "Час не вказано";
    };
    const formatDateOnly = value => {
        if (typeof value !== "string" || !/^\d{4}-\d{2}-\d{2}$/.test(value)) return null;
        const [year, month, day] = value.split("-").map(Number);
        const leap = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
        const days = [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
        return year >= 1 && month >= 1 && month <= 12 && day >= 1 && day <= days[month - 1]
            ? `${value.slice(8, 10)}.${value.slice(5, 7)}.${value.slice(0, 4)}` : null;
    };
    const element = (tag, className, text) => {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (text !== undefined) node.textContent = text;
        return node;
    };

    function updateCounters(container, results, filter) {
        if (!container) return;
        container.querySelectorAll("[data-count]").forEach(node => {
            node.textContent = results.filter(result => matchesStatus(result.status, node.dataset.count)).length;
        });
        container.querySelectorAll("[data-status-filter]").forEach(button => {
            const active = button.dataset.statusFilter === filter;
            button.classList.toggle("is-active", active);
            button.setAttribute("aria-pressed", String(active));
        });
    }

    function createDeclarationSection(result, provider) {
        const hasInventory = Array.isArray(result.declarations) || Number.isInteger(result.declarationTotal);
        const declarations = Array.isArray(result.declarations) ? result.declarations.filter(item => item && typeof item === "object").slice(0, 20) : [];
        const total = Number.isInteger(result.declarationTotal) && result.declarationTotal >= declarations.length ? result.declarationTotal : null;
        const section = element("section", "declaration-results");
        section.dataset.provider = provider.key;
        section.setAttribute("aria-label", provider.title);
        const heading = element("div", "declaration-heading");
        heading.append(element("h4", "", provider.title));
        if (hasInventory) heading.append(element("span", "declaration-count", total !== null ? `Показано ${declarations.length} із ${total}` : `Документів у списку: ${declarations.length}`));
        section.append(heading, element("p", "declaration-provider-note", provider.note), element("p", "identity-notice", "Збіг ПІБ не підтверджує особу. Відсутність декларацій не доводить, що людина не працює або не працювала в державних органах."));
        if (declarations.length) {
            const first = declarations[0];
            const overview = element("section", "person-overview");
            overview.setAttribute("aria-label", "Короткі відомості з першого документа");
            const overviewFields = element("dl", "declaration-meta");
            for (const [label, value] of [["ПІБ", first.fullName], ["Посада", first.position], ["Місце роботи", first.workplace]]) {
                if (value !== null && value !== undefined && String(value).trim()) overviewFields.append(element("dt", "", label), element("dd", "", String(value)));
            }
            if (overviewFields.children.length) {
                overview.append(element("h5", "", "Короткі відомості"), element("p", "field-hint", "З першого знайденого документа. Дані можуть стосуватися тезки й не підтверджують поточну зайнятість."), overviewFields);
                section.append(overview);
            }
            const list = element("ul", "declaration-list");
            list.tabIndex = 0;
            list.setAttribute("aria-label", `Документи: ${provider.title}`);
            declarations.forEach(declaration => {
                const item = element("li", "declaration-item");
                item.append(element("h5", "declaration-name", declaration.fullName || "ПІБ не вказано в результаті"));
                const metadata = element("dl", "declaration-meta");
                const field = (label, value) => {
                    if (value !== null && value !== undefined && String(value).trim()) metadata.append(element("dt", "", label), element("dd", "", String(value)));
                };
                field("Рік декларації", declaration.declarationYear);
                field("Тип декларації", declaration.declarationType);
                field("Тип документа", declaration.documentType);
                field("Посада", declaration.position);
                field("Місце роботи", declaration.workplace);
                if (declaration.submittedAt) field("Подано", formatDate(declaration.submittedAt));
                else field("Подано", formatDateOnly(declaration.submittedDate));
                if (metadata.children.length) item.append(metadata);
                const url = declarationDocumentUrl(declaration.url, declaration.id, provider);
                if (url) {
                    const link = element("a", "declaration-link", `${provider.documentLabel} ↗`);
                    link.href = url; link.target = "_blank"; link.rel = "noopener noreferrer";
                    item.append(link);
                }
                list.append(item);
            });
            section.append(list);
            if ((total !== null && total > declarations.length) || declarations.length === 20) section.append(element("p", "declaration-limit", "Показано до 20 отриманих документів. Повний список перевірте в джерелі."));
        } else if (hasInventory) {
            section.append(element("p", "declaration-empty", "Документів для показу немає. Уточніть ПІБ або перевірте пошук на сайті джерела."));
        } else {
            section.append(element("p", "declaration-unavailable", "Дані джерела не отримано. Причину вказано вище; пошук можна відкрити окремо."));
        }
        const sourceLabel = provider.key === "youcontrol" && new URL(result.profileUrl).pathname !== provider.searchPath
            ? "Відкрити картку на YouControl" : provider.searchLabel;
        const link = element("a", "declaration-link declaration-all", `${sourceLabel} ↗`);
        link.href = result.profileUrl; link.target = "_blank"; link.rel = "noopener noreferrer";
        section.append(link);
        return section;
    }

    function createResultCard(result, manualCheck) {
        const status = statuses[result.status] ? result.status : "unconfirmed";
        const card = element("article", `result-card status-${status}`);
        const heading = element("div", "result-card-heading");
        const identity = element("div", "result-identity");
        const avatar = element("span", "source-avatar", result.site.slice(0, 1));
        avatar.setAttribute("aria-hidden", "true");
        const title = element("div");
        title.append(element("h3", "result-source", result.site), element("p", "result-identifier", result.identifier));
        identity.append(avatar, title);
        heading.append(identity, element("span", `status-pill status-${status}`, result.label || statuses[status].label));
        card.append(heading);
        if (result.reason) card.append(element("p", "result-reason", result.reason));
        const provider = declarationProvider(result.profileUrl);
        const url = safeUrl(result.profileUrl);
        if (url && !provider) {
            const link = element("a", "result-url", `${url} ↗`);
            link.href = url;
            link.target = "_blank";
            link.rel = "noopener noreferrer";
            link.setAttribute("aria-label", `${status === "found" ? "Відкрити профіль" : "Відкрити сторінку перевірки"}: ${result.site}, ${result.identifier}`);
            card.append(link);
        }
        if (manualCheck && url && ["blocked", "unconfirmed", "error", "cancelled"].includes(status)) {
            const actions = element("div", "result-manual-actions");
            const button = element("button", "btn btn-outline-secondary btn-sm manual-check-button", "Перевірити у Chrome вручну");
            button.type = "button";
            button.addEventListener("click", () => manualCheck(result));
            actions.append(button);
            card.append(actions);
        }
        if (provider && !["pending", "checking"].includes(status)) card.append(createDeclarationSection(result, provider));
        const details = element("details", "result-details");
        details.append(element("summary", "", "Деталі перевірки"));
        const list = element("dl");
        const detail = (label, value) => list.append(element("dt", "", label), element("dd", "", value));
        detail("Статус", result.label || statuses[status].label);
        if (result.reason) detail("Причина", result.reason);
        detail("Ознаки", result.evidence || "Додаткових ознак немає.");
        detail(provider ? "Час запиту" : "Перевірено", result.checkedAt ? formatDate(result.checkedAt) : status === "checking" ? "Перевірка триває." : "Ще не перевірено.");
        details.append(list);
        card.append(details);
        return card;
    }

    function initHistory() {
        const history = document.getElementById("historyWorkspace");
        if (!history) return;
        history.querySelectorAll("time[data-local-time]").forEach(time => {
            time.textContent = formatDate(time.dateTime);
        });
        const status = history.querySelector("#historyStatus");
        const site = history.querySelector("#historySite");
        const text = history.querySelector("#historyText");
        const cards = [...history.querySelectorAll("[data-result-card]")];
        if (!status || !site || !text) return;
        const results = cards.map(card => ({ status: card.dataset.status }));
        const filter = () => {
            const query = normalizeText(text.value.trim());
            let visible = 0;
            cards.forEach(card => {
                const matches = matchesStatus(card.dataset.status, status.value)
                    && (site.value === "all" || card.dataset.site === site.value)
                    && (!query || normalizeText(card.textContent).includes(query));
                card.hidden = !matches;
                if (matches) visible++;
            });
            history.querySelectorAll(".history-search").forEach(group => {
                group.hidden = ![...group.querySelectorAll("[data-result-card]")].some(card => !card.hidden);
            });
            history.querySelector("#historyVisibleCount").textContent = `Показано ${visible} із ${cards.length} результатів`;
            history.querySelector("#historyEmpty").hidden = visible > 0;
            updateCounters(history.querySelector("#historyCounters"), results, status.value);
        };
        [status, site].forEach(input => input.addEventListener("change", filter));
        text.addEventListener("input", filter);
        history.querySelectorAll("[data-status-filter]").forEach(button => button.addEventListener("click", () => {
            status.value = button.dataset.statusFilter;
            filter();
        }));
        history.querySelector("#resetHistoryFilters")?.addEventListener("click", () => {
            status.value = "all"; site.value = "all"; text.value = ""; filter();
        });
        filter();
    }

    function initSearch() {
        const workspace = document.getElementById("searchWorkspace");
        if (!workspace) return;
        const byId = id => workspace.querySelector(`#${id}`);
        const form = byId("searchForm");
        const settings = byId("searchSettings");
        const sources = [...workspace.querySelectorAll(".source-option")];
        const fields = byId("identifierFields");
        const startButton = byId("startSearch");
        const cancelButton = byId("cancelSearch");
        const retryButton = byId("retrySearch");
        const resultStatus = byId("resultStatus");
        const resultSite = byId("resultSite");
        const resultText = byId("resultText");
        const storageKey = "MediaParser.lastSearch.v1";
        const selectedByMode = new Map([ ["User", new Set()], ["FullName", new Set()] ]);
        const sourceMetadata = new Map(sources.map(source => [Number(source.dataset.sourceId), source]));
        sources.forEach(source => {
            if (source.querySelector("input").dataset.initialSelected === "true")
                selectedByMode.get(source.dataset.mode)?.add(Number(source.dataset.sourceId));
        });
        let mode = form.querySelector('[name="mode"]:checked')?.value || "User";
        let activeId = null;
        let snapshot = null;
        let submitting = false;
        let working = false;
        let cancelRequested = false;
        let pollTimer = null;
        let generation = 0;
        let connectionFailures = 0;
        const pollsInFlight = new Set();
        let lastResultsFingerprint = null;
        let resultCards = new Map();
        let filteredResults = [];
        let lastAnnouncement = "";

        function showError(message, focus = false) {
            const error = byId("searchError");
            error.textContent = message || "";
            error.hidden = !message;
            if (message && focus) error.focus();
        }
        function saveJob(id) {
            try {
                if (id) sessionStorage.setItem(storageKey, id);
                else sessionStorage.removeItem(storageKey);
            } catch { /* Searching also works when storage is unavailable. */ }
        }
        function endpoint(type, id) {
            return `${workspace.dataset[`${type}Url`].replace(/\/$/, "")}/${encodeURIComponent(id)}`;
        }
        function setBusy(busy) {
            working = busy;
            settings.disabled = busy;
            startButton.disabled = busy || selectedSources().length === 0;
            startButton.textContent = busy ? "Перевірка триває…" : "↗ Почати перевірку";
            form.setAttribute("aria-busy", String(busy));
            workspace.querySelectorAll(".manual-check-button").forEach(button => button.disabled = busy);
        }
        function selectedSources() {
            return sources.filter(source => source.dataset.mode === mode && source.querySelector("input").checked);
        }
        function updateSelection() {
            const selected = selectedSources();
            selectedByMode.set(mode, new Set(selected.map(source => Number(source.dataset.sourceId))));
            byId("selectedSourceCount").textContent = selected.length;
            byId("selectedSourceNames").textContent = selected.length
                ? selected.map(source => source.dataset.sourceName).join(", ")
                : "Оберіть джерела нижче, щоб почати перевірку.";
            const count = [...fields.querySelectorAll("input")].filter(input => input.value.trim()).length;
            byId("searchReadyHint").textContent = selected.length
                ? `Обрано ${selected.length} джерел${count ? ` · до ${selected.length * count} перевірок` : ""}. Результати з’являтимуться під час пошуку.`
                : "Оберіть щонайменше одне сумісне джерело.";
            startButton.disabled = working || submitting || selected.length === 0;
        }
        function validateIdentifiers() {
            fields.querySelectorAll("input").forEach(input => {
                const value = input.value.trim();
                let isYouControlPersonUrl = false;
                if (mode === "FullName") {
                    try {
                        const url = new URL(value);
                        isYouControlPersonUrl = url.origin === "https://youcontrol.com.ua" && !url.username && !url.password
                            && !url.search && !url.hash && /^\/catalog\/individuals\/[a-z0-9]+(?:-[a-z0-9]+)+\/$/.test(url.pathname);
                    } catch { }
                }
                input.maxLength = mode === "FullName" ? 512 : 100;
                input.setCustomValidity(value.length > (isYouControlPersonUrl ? 512 : 100)
                    ? "ПІБ або нікнейм має містити до 100 символів, HTTPS-URL картки YouControl — до 512."
                    : mode === "User" && /\s/.test(value)
                    ? "Нікнейм не може містити пробіли. Для імені та прізвища оберіть відповідний режим."
                    : "");
            });
        }
        function filterSources() {
            const query = normalizeText(byId("sourceSearch").value.trim());
            let visible = 0;
            sources.forEach(source => {
                source.hidden = !!query && !normalizeText(source.dataset.sourceName).includes(query);
                if (!source.hidden) visible++;
            });
            byId("sourcesEmpty").hidden = visible > 0 || sources.length === 0;
        }
        function applyMode(nextMode) {
            mode = nextMode;
            const selection = selectedByMode.get(mode) || new Set();
            sources.forEach(source => {
                const matches = source.dataset.mode === mode;
                const input = source.querySelector("input");
                input.disabled = !matches;
                input.checked = matches && selection.has(Number(source.dataset.sourceId));
                source.classList.toggle("is-incompatible", !matches);
            });
            byId("modeHint").textContent = mode === "User"
                ? "Шукаємо конкретний профіль за нікнеймом."
                : "Пошук за ПІБ у соцмережах і публічних реєстрах.";
            byId("fullNameNotice").hidden = mode !== "FullName";
            byId("sourceModeHint").textContent = mode === "User"
                ? "Джерела пошуку за ПІБ вимкнені для цього режиму."
                : "Джерела профілів за нікнеймом вимкнені для цього режиму.";
            byId("identifierHint").textContent = mode === "User"
                ? "До 5 ідентифікаторів за один пошук. Дублікати перевіряються один раз."
                : "ПІБ — до 100 символів; HTTPS-URL картки YouControl — до 512. Для URL оберіть лише YouControl. До 5 ідентифікаторів за пошук.";
            fields.querySelectorAll("input").forEach(input => {
                input.placeholder = mode === "User" ? "Наприклад, nickname" : "ПІБ або URL картки YouControl";
            });
            validateIdentifiers();
            updateSelection();
        }
        function indexFields() {
            const rows = [...fields.querySelectorAll(".identifier-row")];
            rows.forEach((row, index) => {
                const input = row.querySelector("input");
                input.id = `identifier_${index}`;
                input.name = `Nicknames[${index}]`;
                row.querySelector("label").htmlFor = input.id;
                row.querySelector("label").textContent = `Ідентифікатор ${index + 1}`;
                const remove = row.querySelector("button");
                remove.hidden = rows.length === 1;
                remove.setAttribute("aria-label", `Видалити ідентифікатор ${index + 1}`);
            });
            byId("addIdentifier").disabled = rows.length >= 5;
            updateSelection();
        }
        byId("addIdentifier").addEventListener("click", () => {
            if (working || fields.children.length >= 5) return;
            const row = fields.firstElementChild.cloneNode(true);
            row.querySelector("input").value = "";
            fields.append(row);
            indexFields();
            row.querySelector("input").focus();
        });
        fields.addEventListener("click", event => {
            const button = event.target.closest(".remove-identifier");
            if (!button || working || fields.children.length < 2) return;
            const previous = button.closest(".identifier-row").previousElementSibling;
            button.closest(".identifier-row").remove();
            indexFields();
            (previous || fields.firstElementChild).querySelector("input").focus();
        });
        fields.addEventListener("input", () => { validateIdentifiers(); updateSelection(); });
        form.querySelectorAll('[name="mode"]').forEach(input => input.addEventListener("change", () => applyMode(input.value)));
        sources.forEach(source => source.querySelector("input").addEventListener("change", updateSelection));
        byId("sourceSearch").addEventListener("input", filterSources);
        byId("selectAllSources").addEventListener("click", () => {
            sources.filter(source => source.dataset.mode === mode).forEach(source => source.querySelector("input").checked = true);
            updateSelection();
        });
        byId("clearSources").addEventListener("click", () => {
            sources.forEach(source => source.querySelector("input").checked = false);
            updateSelection();
        });

        async function requestJson(url, method = "GET", body) {
            const controller = new AbortController();
            const timeout = setTimeout(() => controller.abort(), 20000);
            const headers = { Accept: "application/json" };
            if (method !== "GET") headers.RequestVerificationToken = form.querySelector('[name="__RequestVerificationToken"]').value;
            if (body !== undefined) headers["Content-Type"] = "application/json";
            try {
                const response = await fetch(url, {
                    method, headers, credentials: "same-origin", signal: controller.signal,
                    ...(body !== undefined ? { body: JSON.stringify(body) } : {})
                });
                let data = null;
                try { data = await response.json(); } catch { /* Error pages may not be JSON. */ }
                if (!response.ok) {
                    const validation = data?.errors ? Object.values(data.errors).flat().join(" ") : "";
                    const message = data?.error || data?.message || data?.detail || validation;
                    const error = new Error(typeof message === "string" && message.trim() ? message : `Не вдалося виконати запит (HTTP ${response.status}).`);
                    error.status = response.status;
                    throw error;
                }
                if (!data || !data.id || !Array.isArray(data.results)) throw new Error("Сервер повернув неочікувану відповідь. Оновіть сторінку та спробуйте ще раз.");
                return data;
            } catch (error) {
                if (error.name === "AbortError") throw new Error("Сервер не відповідає вчасно. Перевірте з’єднання та спробуйте знову.");
                if (error instanceof TypeError) throw new Error("Немає з’єднання із сервером. Перевірте мережу та спробуйте ще раз.");
                throw error;
            } finally { clearTimeout(timeout); }
        }
        function announce(text) {
            if (text === lastAnnouncement) return;
            byId("progressAnnouncement").textContent = text;
            lastAnnouncement = text;
        }
        function clearPoll() {
            if (pollTimer) clearTimeout(pollTimer);
            pollTimer = null;
        }
        function schedulePoll(delay = 850) {
            clearPoll();
            if (!activeId || (snapshot && terminalStates.has(snapshot.state))) return;
            pollTimer = setTimeout(poll, delay);
        }
        async function poll() {
            clearPoll();
            const id = activeId;
            const version = generation;
            if (!id || (snapshot && terminalStates.has(snapshot.state))) return;
            if (pollsInFlight.has(id)) { schedulePoll(); return; }
            pollsInFlight.add(id);
            try {
                const next = await requestJson(endpoint("progress", id));
                if (version !== generation || id !== activeId) return;
                connectionFailures = 0;
                byId("connectionNotice").hidden = true;
                applySnapshot(next);
                schedulePoll();
            } catch (error) {
                if (version !== generation || id !== activeId || (snapshot && terminalStates.has(snapshot.state))) return;
                if ([401, 403, 404, 410].includes(error.status)) {
                    saveJob(null);
                    activeId = null;
                    setBusy(false);
                    cancelButton.hidden = true;
                    byId("progressHeading").textContent = "Пошук недоступний";
                    byId("progressEyebrow").textContent = "ПОЧНІТЬ НОВУ ПЕРЕВІРКУ";
                    byId("progressCurrent").textContent = "Попередня перевірка більше недоступна у цій сесії.";
                    showError("Цей пошук більше недоступний у поточній сесії. Можна почати нову перевірку.");
                    announce("Пошук недоступний. Почніть нову перевірку.");
                    return;
                }
                connectionFailures++;
                byId("connectionNotice").hidden = false;
                byId("connectionMessage").textContent = "Не вдалося оновити прогрес. З’єднання відновлюється автоматично; отримані результати збережено.";
                schedulePoll(Math.min(5000, 1000 * connectionFailures));
            } finally { pollsInFlight.delete(id); }
        }
        function renderResults() {
            const results = snapshot && Array.isArray(snapshot.plannedResults) ? snapshot.plannedResults : snapshot?.results || [];
            const query = normalizeText(resultText.value.trim());
            filteredResults = results.filter(result => matchesStatus(result.status, resultStatus.value)
                && (resultSite.value === "all" || String(result.siteId) === resultSite.value)
                && (!query || normalizeText([result.site, result.identifier, result.label, result.reason, result.evidence,
                    ...(Array.isArray(result.declarations) ? result.declarations.filter(Boolean).flatMap(declaration => [declaration.fullName, declaration.declarationYear, declaration.declarationType, declaration.documentType, declaration.position, declaration.workplace]) : [])].join(" ")).includes(query)));
            filteredResults.sort((left, right) => (statuses[left.status]?.order ?? 9) - (statuses[right.status]?.order ?? 9)
                || collator.compare(left.site, right.site) || collator.compare(left.identifier, right.identifier));
            const list = byId("resultList");
            const desiredKeys = new Set();
            filteredResults.forEach((result, index) => {
                const key = `${result.siteId}:${result.identifier}`;
                desiredKeys.add(key);
                const metadata = sourceMetadata.get(Number(result.siteId));
                const canCheckManually = snapshot && terminalStates.has(snapshot.state) && metadata?.dataset.manualSupported === "true";
                const signature = `${JSON.stringify(result)}:${canCheckManually}`;
                let cached = resultCards.get(key);
                if (!cached || cached.signature !== signature) {
                    const open = cached?.node.querySelector("details")?.open;
                    const node = createResultCard(result, canCheckManually ? item => {
                        if (activeId && !working && !submitting)
                            beginSearch(endpoint("manual", activeId), { siteId: Number(item.siteId), identifier: item.identifier }, "manual");
                    } : null);
                    if (open) node.querySelector("details").open = true;
                    if (cached?.node.parentNode) cached.node.replaceWith(node);
                    cached = { node, signature };
                    resultCards.set(key, cached);
                }
                if (list.children[index] !== cached.node) list.insertBefore(cached.node, list.children[index] || null);
            });
            resultCards.forEach((cached, key) => {
                if (!desiredKeys.has(key)) cached.node.remove();
            });
            byId("visibleResultCount").textContent = `Показано ${filteredResults.length} із ${results.length} результатів`;
            byId("resultsEmpty").hidden = filteredResults.length > 0;
            updateCounters(byId("resultCounters"), results, resultStatus.value);
        }
        function applySnapshot(next) {
            if (snapshot?.id === next.id && terminalStates.has(snapshot.state) && !terminalStates.has(next.state)) return;
            snapshot = next;
            activeId = next.id;
            saveJob(activeId);
            const terminal = terminalStates.has(next.state);
            setBusy(!terminal);
            cancelRequested = !terminal && (cancelRequested || !!next.cancellationRequested);
            byId("searchProgress").hidden = false;
            byId("progressEyebrow").textContent = terminal ? "ПЕРЕВІРКУ ЗАВЕРШЕНО" : "ПЕРЕВІРКА ТРИВАЄ";
            const labels = { queued: "Пошук у черзі", running: "Перевіряємо джерела", completed: "Пошук завершено", cancelled: "Пошук зупинено", failed: "Пошук завершився помилкою" };
            byId("progressHeading").textContent = cancelRequested ? "Зупиняємо пошук…" : (labels[next.state] || "Готуємо пошук");
            const completed = Math.max(0, Number(next.completed) || 0);
            const total = Math.max(0, Number(next.total) || 0);
            const percent = total ? Math.min(100, Math.round(completed / total * 100)) : 0;
            byId("progressCount").textContent = `${completed} / ${total}`;
            byId("interactiveNotice").hidden = terminal || !next.isInteractive || next.state !== "running";
            const captchaProgress = byId("automaticCaptchaProgress");
            captchaProgress.hidden = !next.automaticCaptcha && !next.progressMessage;
            captchaProgress.textContent = next.progressMessage || (next.automaticCaptcha
                ? (terminal
                    ? `Для цієї перевірки було ввімкнено 2Captcha. Повтор невдалих успадковує опцію та отримує новий ліміт до ${workspace.dataset.captchaTaskLimit || "3"} платних завдань.`
                    : `2Captcha увімкнено для цього пошуку: до ${workspace.dataset.captchaTaskLimit || "3"} платних завдань. Запити створюються лише для підтримуваних капч.`)
                : "");
            const progress = byId("searchProgressBar");
            progress.value = percent;
            progress.textContent = `${percent}%`;
            progress.setAttribute("aria-valuetext", `Перевірено ${completed} із ${total} джерел`);
            byId("progressCurrent").textContent = terminal
                ? (next.state === "cancelled" ? "Отримані результати збережено. Решту перевірок зупинено." : next.state === "failed" ? "Отримані результати доступні нижче." : "Усі заплановані перевірки виконано.")
                : next.currentSite ? `${next.currentSite}${next.currentIdentifier ? ` · ${next.currentIdentifier}` : ""}`
                    : next.state === "queued" ? "Очікуємо вільного виконавця…" : "Готуємо наступне джерело…";
            cancelButton.hidden = terminal;
            cancelButton.disabled = cancelRequested;
            cancelButton.textContent = cancelRequested ? "Зупиняємо…" : "Зупинити пошук";
            retryButton.hidden = !terminal || !next.canRetry;
            retryButton.disabled = !terminal;
            const exportLink = byId("exportSearch");
            exportLink.hidden = !terminal || next.results.length === 0;
            exportLink.href = endpoint("export", activeId);
            if (next.state === "failed" && next.error) showError(next.error);
            announce(terminal ? `${labels[next.state]}. Перевірено ${completed} із ${total}.` : `Перевірено ${completed} із ${total}${cancelRequested ? ". Очікуємо зупинки." : "."}`);
            if (terminal) {
                clearPoll();
                byId("connectionNotice").hidden = true;
            }
            const displayResults = Array.isArray(next.plannedResults) ? next.plannedResults : next.results;
            byId("jobResults").hidden = displayResults.length === 0 && !terminal;
            const fingerprint = JSON.stringify(displayResults);
            if (fingerprint !== lastResultsFingerprint) {
                lastResultsFingerprint = fingerprint;
                const selectedSite = resultSite.value;
                const sites = [...new Map(displayResults.map(result => [String(result.siteId), result.site])).entries()]
                    .sort((left, right) => collator.compare(left[1], right[1]));
                resultSite.replaceChildren(new Option("Усі джерела", "all"), ...sites.map(([id, site]) => new Option(site, id)));
                resultSite.value = sites.some(([id]) => id === selectedSite) ? selectedSite : "all";
                renderResults();
            } else if (terminal) renderResults();
        }
        async function beginSearch(url, body, context = "search") {
            if (working || submitting) return;
            submitting = true;
            generation++;
            clearPoll();
            showError(null);
            setBusy(true);
            retryButton.disabled = true;
            byId("searchProgress").hidden = false;
            byId("progressHeading").textContent = context === "manual" ? "Готуємо ручну перевірку…" : context === "retry" ? "Готуємо повторну перевірку…" : "Готуємо пошук…";
            byId("progressEyebrow").textContent = "ПЕРЕВІРКА ТРИВАЄ";
            byId("progressCurrent").textContent = "Передаємо запит до черги перевірок…";
            byId("interactiveNotice").hidden = true;
            byId("automaticCaptchaProgress").hidden = true;
            cancelButton.hidden = true;
            try {
                const next = await requestJson(url, "POST", body);
                cancelRequested = false;
                connectionFailures = 0;
                snapshot = null;
                resultCards = new Map();
                byId("resultList").replaceChildren();
                lastResultsFingerprint = null;
                resultStatus.value = "all";
                resultSite.value = "all";
                resultText.value = "";
                byId("retryNotice").hidden = context === "search";
                byId("retryNotice").textContent = context === "manual"
                    ? "Ручна перевірка одного джерела. Попередні результати збережено."
                    : "Повторна перевірка проблемних джерел. Попередні результати залишаються в історії.";
                applySnapshot(next);
                schedulePoll();
                byId("searchProgress").scrollIntoView({ behavior: matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth", block: "nearest" });
            } catch (error) {
                setBusy(false);
                retryButton.disabled = false;
                if (snapshot && terminalStates.has(snapshot.state)) applySnapshot(snapshot);
                else byId("searchProgress").hidden = true;
                showError(error.message || "Не вдалося почати пошук. Спробуйте ще раз.", true);
                if (error.status === 409) {
                    const reconnect = element("button", "btn btn-outline-secondary btn-sm mt-2", "Відкрити активний пошук");
                    reconnect.type = "button";
                    reconnect.addEventListener("click", () => location.reload());
                    byId("searchError").append(document.createElement("br"), reconnect);
                }
            } finally {
                submitting = false;
                updateSelection();
            }
        }
        form.addEventListener("submit", event => {
            event.preventDefault();
            if (working || submitting) return;
            if (!form.reportValidity()) return;
            const identifiers = [...new Set([...fields.querySelectorAll("input")].map(input => input.value.trim()).filter(Boolean))];
            const sourceIds = selectedSources().map(source => Number(source.dataset.sourceId));
            if (!identifiers.length) { showError("Вкажіть хоча б один ідентифікатор.", true); return; }
            if (!sourceIds.length) { showError("Оберіть хоча б одне джерело для цього режиму.", true); return; }
            const automaticCaptcha = byId("automaticCaptcha").checked && !byId("automaticCaptcha").disabled;
            beginSearch(workspace.dataset.startUrl, { identifiers, mode, sourceIds, automaticCaptcha });
        });
        cancelButton.addEventListener("click", async () => {
            if (!activeId || !snapshot || terminalStates.has(snapshot.state) || cancelRequested) return;
            const id = activeId;
            const version = generation;
            cancelRequested = true;
            cancelButton.disabled = true;
            cancelButton.textContent = "Зупиняємо…";
            byId("progressHeading").textContent = "Зупиняємо пошук…";
            try {
                const next = await requestJson(endpoint("cancel", id), "POST");
                if (id !== activeId || version !== generation) return;
                applySnapshot(next);
                schedulePoll(500);
            } catch (error) {
                if (id !== activeId || version !== generation || terminalStates.has(snapshot?.state)) return;
                cancelRequested = false;
                applySnapshot(snapshot);
                showError(`Не вдалося передати запит зупинки. ${error.message}`);
                schedulePoll();
            }
        });
        retryButton.addEventListener("click", () => {
            if (activeId && snapshot?.canRetry && terminalStates.has(snapshot.state)) beginSearch(endpoint("retry", activeId), undefined, "retry");
        });
        byId("reconnectSearch").addEventListener("click", () => { connectionFailures = 0; schedulePoll(0); });
        [resultStatus, resultSite].forEach(input => input.addEventListener("change", renderResults));
        resultText.addEventListener("input", renderResults);
        byId("resultCounters").querySelectorAll("[data-status-filter]").forEach(button => button.addEventListener("click", () => {
            resultStatus.value = button.dataset.statusFilter;
            renderResults();
        }));
        byId("resetResultFilters").addEventListener("click", () => {
            resultStatus.value = "all"; resultSite.value = "all"; resultText.value = ""; renderResults();
        });
        applyMode(mode);
        indexFields();
        let storedJob = workspace.dataset.activeJobId || null;
        try { storedJob ||= sessionStorage.getItem(storageKey); } catch { /* Storage is optional. */ }
        if (storedJob) {
            activeId = storedJob;
            setBusy(true);
            byId("searchProgress").hidden = false;
            byId("progressCurrent").textContent = "Відновлюємо останню перевірку…";
            announce("Відновлюємо останню перевірку.");
            poll();
        }
    }

    initHistory();
    initSearch();
})();
