// Conciliação entre dois recursos que disputam o mesmo alvo de soltura.
//
// O drop de ARQUIVOS na janela precisa do caminho completo, que a API web não
// expõe — por isso o WebView2 roda com AllowExternalDrop=false, deixando o evento
// chegar ao formulário WinForms (DataFormats.FileDrop).
//
// Só que esse flag não desliga apenas o drop VINDO DE FORA: ele tira o WebView2 de
// alvo de soltura por inteiro, e com isso o drag-and-drop DENTRO da página também
// morre. A sessão de arraste passa a cair no formulário, cujo DragEnter responde
// "None" por não haver arquivo algum — daí o cursor de bloqueado ao tentar arrastar
// uma coluna para agrupar.
//
// Como as duas coisas se distinguem pela ORIGEM, dá para ter as duas: um arraste
// interno sempre começa com o botão pressionado sobre um elemento draggable da
// página; um arquivo vindo do Explorer, nunca. Então liberamos o WebView2 nesse
// pressionar e devolvemos o flag ao normal quando o arraste termina.
//
// A troca acontece no pointerdown, e não no dragstart: o navegador só inicia a
// sessão depois de alguns pixels de movimento, o que dá folga para o flag valer
// antes. Se por algum motivo não valer a tempo, o pior caso é o comportamento
// atual — nada regride.
window.clefInternalDrag = (function () {
    let dotNetRef = null;
    let ativo = false;

    function definir(interno) {
        if (!dotNetRef || ativo === interno) return;
        ativo = interno;
        try {
            dotNetRef.invokeMethodAsync('SetInternalDragMode', interno).catch(() => {
                // invokeMethodAsync falha de forma assíncrona quando o componente já saiu.
                window.clefInternalDrag.unregister();
            });
        } catch {
            // Componente descartado: para de tentar em vez de repetir o erro a cada gesto.
            window.clefInternalDrag.unregister();
        }
    }

    const aoPressionar = e => {
        if (e.target instanceof Element && e.target.closest('[draggable="true"]')) definir(true);
    };

    // Solto o botão ou encerrada a sessão, volta ao padrão. O pointerup cobre o
    // clique que não virou arraste; o dragend, o arraste de fato.
    const aoSoltar = () => definir(false);

    return {
        register: function (ref) {
            this.unregister();
            dotNetRef = ref;
            document.addEventListener('pointerdown', aoPressionar, true);
            document.addEventListener('pointerup', aoSoltar, true);
            document.addEventListener('dragend', aoSoltar, true);
            window.addEventListener('blur', aoSoltar);
        },

        unregister: function () {
            document.removeEventListener('pointerdown', aoPressionar, true);
            document.removeEventListener('pointerup', aoSoltar, true);
            document.removeEventListener('dragend', aoSoltar, true);
            window.removeEventListener('blur', aoSoltar);
            dotNetRef = null;
            ativo = false;
        }
    };
})();
