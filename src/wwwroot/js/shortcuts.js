// Atalhos de teclado do ClefExplorer.
//
// Um listener no document (fase de captura) traduz combinações em nomes de ação e
// os entrega ao componente Blazor. Fica em JS porque o Blazor só recebe eventos de
// teclado de elementos focados — um visualizador de log precisa dos atalhos
// funcionando com o foco em qualquer lugar da janela.
window.clefShortcuts = (function () {
    let dotNetRef = null;
    let handler = null;

    // Campos de texto: só Escape passa. Caso contrário, digitar "o" numa busca com
    // Ctrl pressionado, ou usar as setas para navegar no texto, disparariam ações.
    function isTyping(el) {
        if (!el) return false;
        const tag = el.tagName;
        return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || el.isContentEditable === true;
    }

    function resolve(e) {
        const ctrl = e.ctrlKey || e.metaKey;

        if (ctrl && !e.altKey) {
            switch (e.key.toLowerCase()) {
                case 'o': return e.shiftKey ? 'open-folder' : 'open-file';
                case 'f': return 'focus-search';
                case 'e': return 'export';
                case 'l': return 'toggle-tail';
            }
        }

        if (e.key === 'F5') return 'reload';

        // Esc NÃO é suportado como atalho: neste host o WebView2 o consome antes de
        // qualquer ponto alcançável. Verificado — não chega a um listener em document
        // nem em window (fase de captura), nem ao ProcessCmdKey do formulário, nem a um
        // IMessageFilter da thread de UI; os próprios popovers da Omni também não fecham
        // com ele. As ações equivalentes seguem no mouse: o "x" do campo de busca limpa a
        // pesquisa e o "x" do painel fecha o detalhe.
        if (e.key === 'ArrowDown') return 'next';
        if (e.key === 'ArrowUp') return 'previous';

        return null;
    }

    return {
        register: function (ref) {
            this.unregister();
            dotNetRef = ref;

            handler = function (e) {
                const action = resolve(e);
                if (!action) return;

                // Com o cursor num campo, só Escape (limpar busca / fechar detalhe) age.
                if (isTyping(document.activeElement) && action !== 'escape') return;

                e.preventDefault();

                if (action === 'focus-search') {
                    const input = document.querySelector('.clef-header-search input');
                    if (input) { input.focus(); input.select(); }
                    return;
                }

                try {
                    dotNetRef.invokeMethodAsync('OnShortcut', action);
                } catch (err) {
                    // Componente já descartado: para de escutar em vez de repetir o erro.
                    window.clefShortcuts.unregister();
                }
            };

            // window + captura: é o primeiro alvo do caminho de captura, antes de
            // document. Com o listener em `document` o Esc nunca chegava — algo
            // registrado antes (overlay/hotkey da lib) o consumia no mesmo nível.
            window.addEventListener('keydown', handler, true);
        },

        unregister: function () {
            if (handler) {
                window.removeEventListener('keydown', handler, true);
                handler = null;
            }
            dotNetRef = null;
        }
    };
})();
