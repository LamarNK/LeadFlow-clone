(function () {
    var editor = null;
    var nodeMeta = {};
    var instances = [];
    var initialRoute = null;

    function parseJson(raw, fallback) {
        if (!raw) return fallback;
        try {
            return JSON.parse(raw);
        } catch (e) {
            return fallback;
        }
    }

    function createClientId() {
        if (window.crypto && typeof window.crypto.randomUUID === 'function') {
            return window.crypto.randomUUID();
        }
        return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (c) {
            var r = Math.random() * 16 | 0;
            var v = c === 'x' ? r : (r & 0x3 | 0x8);
            return v.toString(16);
        });
    }

    function escapeHtml(value) {
        return String(value == null ? '' : value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    function getRoot() {
        return document.querySelector('[data-distribution-editor]');
    }

    function getForm(root) {
        return root ? root.closest('[data-distribution-form]') || document.querySelector('[data-distribution-form]') : null;
    }

    function buildNodeHtml(instance) {
        return '<div class="bitrix-node__title">' + escapeHtml(instance.label || instance.name) + '</div>' +
            (instance.signature ? '<div class="bitrix-node__signature">' + escapeHtml(instance.name) + '</div>' : '') +
            '<button type="button" class="bitrix-node__remove" data-distribution-remove-node>Удалить</button>';
    }

    function registerNode(drawflowId, instance, persistedId) {
        nodeMeta[drawflowId] = {
            clientId: persistedId || createClientId(),
            persistedId: persistedId || null,
            bitrixInstanceId: instance.id,
            sortOrder: 0,
            editorPositionX: 0,
            editorPositionY: 0
        };
    }

    function resolveNodeId(meta) {
        return meta.persistedId || meta.clientId || null;
    }

    function addNode(instance, x, y, persistedId) {
        if (!editor) return null;
        var html = buildNodeHtml(instance);
        var drawflowId = editor.addNode(
            'bitrix',
            1,
            1,
            x || 120,
            y || 120,
            'bitrix-node',
            { bitrixInstanceId: instance.id, label: instance.label },
            html
        );
        registerNode(drawflowId, instance, persistedId);
        return drawflowId;
    }

    function renderPalette(root) {
        var list = root.querySelector('[data-distribution-palette-list]');
        if (!list) return;
        list.innerHTML = instances.map(function (instance) {
            return '<div class="settings-distribution-palette-item" draggable="true" data-bitrix-instance-id="' + escapeHtml(instance.id) + '">' +
                '<strong>' + escapeHtml(instance.label || instance.name) + '</strong>' +
                '<button type="button" class="settings-distribution-palette-add" data-distribution-add-instance="' + escapeHtml(instance.id) + '">Добавить</button>' +
                '</div>';
        }).join('');

        if (!instances.length) {
            list.innerHTML = '<p class="settings-distribution-palette-hint">Нет активных Битриксов. Сначала добавьте их на вкладке «Битриксы».</p>';
        }
    }

    function loadRoute(route) {
        if (!editor || !route || !Array.isArray(route.nodes)) return;
        var created = [];
        route.nodes.forEach(function (node) {
            var instance = instances.find(function (x) { return x.id === node.bitrixInstanceId; });
            if (!instance) {
                instance = {
                    id: node.bitrixInstanceId,
                    name: node.bitrixName,
                    signature: node.bitrixSignature,
                    label: node.bitrixSignature || node.bitrixName
                };
            }
            var drawflowId = addNode(
                instance,
                node.editorPositionX || 120,
                node.editorPositionY || 120,
                node.id
            );
            if (drawflowId) {
                created.push({ drawflowId: drawflowId, node: node });
            }
        });
        created.forEach(function (item) {
            if (!item.node.parentNodeId) return;
            var parentDrawflowId = Object.keys(nodeMeta).find(function (key) {
                var meta = nodeMeta[key];
                return meta.persistedId === item.node.parentNodeId || meta.clientId === item.node.parentNodeId;
            });
            if (parentDrawflowId) {
                editor.addConnection(Number(parentDrawflowId), item.drawflowId, 'output_1', 'input_1');
            }
        });
    }

    function collectNodes() {
        if (!editor) return [];
        var exported = editor.export();
        var drawflowNodes = (((exported || {}).drawflow || {}).Home || {}).data || {};
        var connectionParents = {};
        Object.keys(drawflowNodes).forEach(function (key) {
            var node = drawflowNodes[key];
            var outputs = node.outputs || {};
            Object.keys(outputs).forEach(function (outputKey) {
                (outputs[outputKey].connections || []).forEach(function (conn) {
                    connectionParents[String(conn.node)] = String(key);
                });
            });
        });

        var orderedKeys = Object.keys(drawflowNodes).sort(function (a, b) {
            var nodeA = drawflowNodes[a];
            var nodeB = drawflowNodes[b];
            var yDiff = (nodeA.pos_y || 0) - (nodeB.pos_y || 0);
            if (Math.abs(yDiff) > 8) return yDiff;
            return (nodeA.pos_x || 0) - (nodeB.pos_x || 0);
        });

        return orderedKeys.map(function (key, index) {
            var node = drawflowNodes[key];
            var meta = nodeMeta[key] || {};
            var parentDrawflowId = connectionParents[key];
            var parentMeta = parentDrawflowId ? nodeMeta[parentDrawflowId] : null;
            var nodeId = resolveNodeId(meta);
            var parentNodeId = parentMeta ? resolveNodeId(parentMeta) : null;
            return {
                id: nodeId,
                parentNodeId: parentNodeId,
                bitrixInstanceId: (node.data && node.data.bitrixInstanceId) || meta.bitrixInstanceId,
                sortOrder: index,
                editorPositionX: node.pos_x || 0,
                editorPositionY: node.pos_y || 0
            };
        }).filter(function (node) { return !!node.bitrixInstanceId; });
    }

    function syncNodesJson(form) {
        var input = form.querySelector('[data-distribution-nodes-json]');
        if (!input) return;
        input.value = JSON.stringify(collectNodes());
    }

    function bindForm(form, root) {
        form.addEventListener('submit', function () {
            syncNodesJson(form);
        });

        var saveBtn = form.querySelector('[data-distribution-save]');
        if (saveBtn) {
            saveBtn.addEventListener('click', function () {
                syncNodesJson(form);
            });
        }

        var resetBtn = root.querySelector('[data-distribution-reset]');
        if (resetBtn) {
            resetBtn.addEventListener('click', function () {
                if (!editor) return;
                editor.clear();
                nodeMeta = {};
                loadRoute(initialRoute);
            });
        }
    }

    function bindPalette(root) {
        root.addEventListener('click', function (e) {
            var addBtn = e.target.closest('[data-distribution-add-instance]');
            if (!addBtn) return;
            var instance = instances.find(function (x) { return x.id === addBtn.getAttribute('data-distribution-add-instance'); });
            if (instance) addNode(instance, 140 + Math.random() * 80, 100 + Math.random() * 80, null);
        });

        root.addEventListener('dragstart', function (e) {
            var item = e.target.closest('[data-bitrix-instance-id]');
            if (!item) return;
            e.dataTransfer.setData('text/bitrix-instance-id', item.getAttribute('data-bitrix-instance-id'));
        });

        var canvas = root.querySelector('[data-distribution-canvas]');
        if (!canvas) return;
        canvas.addEventListener('dragover', function (e) { e.preventDefault(); });
        canvas.addEventListener('drop', function (e) {
            e.preventDefault();
            var instanceId = e.dataTransfer.getData('text/bitrix-instance-id');
            var instance = instances.find(function (x) { return x.id === instanceId; });
            if (!instance || !editor) return;
            var rect = canvas.getBoundingClientRect();
            addNode(instance, e.clientX - rect.left, e.clientY - rect.top, null);
        });
    }

    function bindNodeActions(root) {
        root.addEventListener('click', function (e) {
            var removeBtn = e.target.closest('[data-distribution-remove-node]');
            if (!removeBtn || !editor) return;
            var nodeEl = removeBtn.closest('.drawflow-node');
            if (!nodeEl || !nodeEl.id) return;
            var drawflowId = nodeEl.id.replace('node-', '');
            editor.removeNodeId('node-' + drawflowId);
            delete nodeMeta[drawflowId];
        });
    }

    function initDistributionEditor() {
        var root = getRoot();
        if (!root || typeof Drawflow === 'undefined') return;
        if (root.dataset.distributionBound === '1') return;
        root.dataset.distributionBound = '1';

        instances = parseJson(root.getAttribute('data-instances-json'), []);
        initialRoute = parseJson(root.getAttribute('data-route-json'), { nodes: [] });

        var canvas = root.querySelector('[data-distribution-canvas]');
        if (!canvas) return;

        editor = new Drawflow(canvas);
        editor.reroute = true;
        editor.start();
        nodeMeta = {};

        renderPalette(root);
        loadRoute(initialRoute);
        bindPalette(root);
        bindNodeActions(root);

        var form = getForm(root);
        if (form) bindForm(form, root);
    }

    initDistributionEditor();
    document.addEventListener('orbita:content-updated', initDistributionEditor);
})();