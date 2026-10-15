/**
 * Plugin Kazam per l'editor di pianta del portale (self-host): notifica al parent la selezione
 * corrente del canvas via postMessage, cosi' "Riassocia cella" parte dal segnaposto selezionato.
 */
Draw.loadPlugin(function (ui) {
    try {
        window.kazamUi = ui;   // diagnostica/collaudo del portale (stessa origine)
        var graph = ui.editor.graph;

        // I segnaposto hanno editable=0 (testo = riferimento alla cella, non modificabile a
        // mano) e draw.io colora la selezione delle celle non editabili di ROSSO come se
        // fossero bloccate: qui restano spostabili/ridimensionabili, quindi selezione normale.
        mxVertexHandler.prototype.getSelectionColor = function () {
            return mxConstants.VERTEX_SELECTION_COLOR;
        };

        // Una pianta = una pagina: via la barra dei tab in basso (e con lei l'aggiunta di
        // pagine, che il salvataggio non gestisce) e i link esterni di branding draw.io.
        if (ui.setTabContainerVisible) ui.setTabContainerVisible(false);
        var esterni = document.querySelectorAll(
            'a[href*="github.com"], a[href*="drawio"], a[href*="draw.io"], a[href*="diagrams.net"]');
        for (var j = 0; j < esterni.length; j++) esterni[j].parentNode.removeChild(esterni[j]);
        graph.getSelectionModel().addListener(mxEvent.CHANGE, function () {
            var celle = graph.getSelectionCells() || [];
            var ids = [];
            for (var i = 0; i < celle.length; i++) {
                if (celle[i] != null && celle[i].id != null) ids.push(celle[i].id);
            }
            if (window.parent !== window) {
                window.parent.postMessage(JSON.stringify({ event: 'kazamSelect', ids: ids }), '*');
            }
        });

        // kazamFocus dal parent: porta in evidenza una cella appena inserita/riassociata
        // (a zoom 25% su piante grandi un segnaposto nuovo di 54x24 e' invisibile).
        window.addEventListener('message', function (evt) {
            try {
                if (typeof evt.data !== 'string' || evt.data.length === 0) return;
                var msg = JSON.parse(evt.data);
                if (msg.action !== 'kazamFocus' || msg.id == null) return;
                var cell = graph.model.getCell(msg.id);
                if (cell == null) return;
                if (graph.view.scale < 0.75) graph.zoomTo(0.75);
                graph.setSelectionCell(cell);
                graph.scrollCellToVisible(cell, true);
            } catch (e) { /* il focus e' un aiuto, mai un blocco */ }
        });
    } catch (e) { /* la selezione e' un aiuto, mai un blocco */ }
});
