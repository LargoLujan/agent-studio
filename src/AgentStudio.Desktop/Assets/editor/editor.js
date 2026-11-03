// Monaco bridge for Agent Studio. Protocol: see Controls/Editor/EditorMessages.cs.
// "versionId" is Monaco's alternative version id (returns to its old value after undoing back to the saved text).
(function () {
    'use strict';

    var webview = window.chrome && window.chrome.webview;
    var editorHost = document.getElementById('editor');
    var diffHost = document.getElementById('diff');

    var monacoApi = null;
    var editor = null;
    var diffEditor = null;
    var diffModels = [];
    var inDiff = false;
    var savedVersionId = 0;

    function post(message) {
        if (webview) {
            webview.postMessage(message);
        }
    }

    function fail(error) {
        post({ type: 'error', message: String(error && error.message ? error.message : error) });
    }

    function currentModel() {
        return editor ? editor.getModel() : null;
    }

    function versionOf(model) {
        return model.getAlternativeVersionId();
    }

    function reportChange() {
        var model = currentModel();
        if (model) {
            var version = versionOf(model);
            post({ type: 'changed', versionId: version, isDirty: version !== savedVersionId });
        }
    }

    // ---- Theme data ---------------------------------------------------------------------------------------------
    // Colours mirror Themes/Dark.xaml; the token each one comes from is named after it. WCAG 2.x AA is enforced by
    // tests/AgentStudio.Tests/Desktop/Contrast/WebThemeContrastTests.cs, which PARSES the two literals below, so keep
    // their layout: one 'key': '#RRGGBB' (or '#RRGGBBAA', alpha last) per line in THEME_COLORS and one rule per line
    // in THEME_RULES.
    //
    // The theme does not inherit Monaco's vs-dark rules: every token colour has to be in this file to be checked.
    // They are the vs-dark values of monaco-editor 0.57.0, lightened (towards white, as little as possible) only
    // where they fell under 4.5:1 on the editor or on the current-line background. Tokens of languages the editor
    // does not open (pug, scss, swift, php) were dropped. Translucent overlays that sit behind text (selections,
    // word highlight, find matches, bracket match) stay at or below 33% accent so syntax colours keep >= 3:1 on them.
    var THEME_COLORS = {
        'editor.background': '#0A1020',                    // Background
        'editor.foreground': '#E8EEF9',                    // TextPrimary
        'editor.lineHighlightBackground': '#101A30',       // Surface
        'editor.selectionBackground': '#2E68F955',         // Accent, 33%
        'editor.inactiveSelectionBackground': '#2E68F933', // Accent, 20%
        'editor.selectionHighlightBackground': '#2E68F933',
        'editor.wordHighlightBackground': '#2E68F933',
        'editor.wordHighlightStrongBackground': '#2E68F94D',
        'editor.wordHighlightTextBackground': '#2E68F933',
        'editor.findMatchBackground': '#2E68F955',
        'editor.findMatchBorder': '#82A4FB',               // Accent tinted towards white: 7:1 on the editor
        'editor.findMatchHighlightBackground': '#2E68F94D',
        'editor.findRangeHighlightBackground': '#2E68F91A',
        'editorBracketMatch.background': '#2E68F94D',
        'editorBracketMatch.border': '#82A4FB',
        'editorCursor.foreground': '#2E68F9',              // Accent
        'editorLineNumber.foreground': '#6F82A6',          // Muted text: 4.9:1 on the editor
        'editorLineNumber.activeForeground': '#E8EEF9',
        'editorIndentGuide.background1': '#243558',        // Border (decorative)
        'editorIndentGuide.activeBackground1': '#5F7396',  // BorderStrong (decorative)
        'editorWidget.background': '#16223D',              // SurfaceRaised
        'editorWidget.border': '#243558',                  // Border (decorative)
        'editorWidget.foreground': '#E8EEF9',
        'editorSuggestWidget.background': '#16223D',
        'editorSuggestWidget.border': '#243558',
        'editorSuggestWidget.foreground': '#E8EEF9',
        'editorSuggestWidget.selectedBackground': '#2E68F9', // Accent
        'editorSuggestWidget.selectedForeground': '#FFFFFF', // OnAccent
        'editorSuggestWidget.highlightForeground': '#82A4FB',
        'editorSuggestWidget.focusHighlightForeground': '#FFFFFF',
        'editorHoverWidget.background': '#16223D',
        'editorHoverWidget.foreground': '#E8EEF9',
        'editorHoverWidget.border': '#243558',
        'input.background': '#101A30',                     // Surface
        'input.foreground': '#E8EEF9',
        'input.border': '#5F7396',                         // BorderStrong: the boundary of a text field
        'input.placeholderForeground': '#9FB0CB',          // TextSecondary
        'focusBorder': '#2E68F9',                          // Accent
        'scrollbarSlider.background': '#5F7396',           // BorderStrong: a thumb must be >= 3:1 on its track
        'scrollbarSlider.hoverBackground': '#7A8DB0',
        'scrollbarSlider.activeBackground': '#2E68F9',
        'diffEditor.insertedTextBackground': '#2FD08A28',  // Success, 16%
        'diffEditor.removedTextBackground': '#FF5C6C28'    // Error, 16%
    };

    var THEME_RULES = [
        { token: 'invalid', foreground: 'f44747' },
        { token: 'emphasis', fontStyle: 'italic' },
        { token: 'strong', fontStyle: 'bold' },
        { token: 'variable', foreground: '74B0DF' },
        { token: 'variable.predefined', foreground: '6D83BB' },
        { token: 'variable.parameter', foreground: '9CDCFE' },
        { token: 'constant', foreground: '569CD6' },
        { token: 'comment', foreground: '669055' },
        { token: 'number', foreground: 'B5CEA8' },
        { token: 'number.hex', foreground: '5BB498' },
        { token: 'regexp', foreground: 'B76C99' },
        { token: 'annotation', foreground: 'cc6666' },
        { token: 'type', foreground: '3DC9B0' },
        { token: 'delimiter', foreground: 'DCDCDC' },
        { token: 'delimiter.html', foreground: '848484' },
        { token: 'delimiter.xml', foreground: '848484' },
        { token: 'tag', foreground: '569CD6' },
        { token: 'meta.tag', foreground: 'CE9178' },
        { token: 'metatag', foreground: 'DD6A6F' },
        { token: 'metatag.content.html', foreground: '9CDCFE' },
        { token: 'metatag.html', foreground: '569CD6' },
        { token: 'metatag.xml', foreground: '569CD6' },
        { token: 'key', foreground: '9CDCFE' },
        { token: 'string.key.json', foreground: '9CDCFE' },
        { token: 'string.value.json', foreground: 'CE9178' },
        { token: 'attribute.name', foreground: '9CDCFE' },
        { token: 'attribute.value', foreground: 'CE9178' },
        { token: 'attribute.value.number.css', foreground: 'B5CEA8' },
        { token: 'attribute.value.unit.css', foreground: 'B5CEA8' },
        { token: 'attribute.value.hex.css', foreground: 'D4D4D4' },
        { token: 'string', foreground: 'CE9178' },
        { token: 'string.sql', foreground: 'FF5C6C' },
        { token: 'keyword', foreground: '569CD6' },
        { token: 'keyword.flow', foreground: 'C586C0' },
        { token: 'keyword.json', foreground: 'CE9178' },
        { token: 'operator.sql', foreground: '778899' },
        { token: 'predefined.sql', foreground: 'FF00FF' }
    ];

    function defineTheme(monaco) {
        monaco.editor.defineTheme('agentstudio-dark', {
            base: 'vs-dark',
            inherit: false,
            rules: THEME_RULES,
            colors: THEME_COLORS
        });
    }

    function bindSave(codeEditor) {
        codeEditor.addCommand(monacoApi.KeyMod.CtrlCmd | monacoApi.KeyCode.KeyS, function () {
            post({ type: 'saveRequested' });
        });
    }

    function leaveDiff() {
        if (!inDiff) {
            return;
        }
        inDiff = false;
        diffHost.style.display = 'none';
        editorHost.style.display = 'block';
        if (diffEditor) {
            diffEditor.setModel(null);
        }
        diffModels.forEach(function (m) { m.dispose(); });
        diffModels = [];
        editor.layout();
    }

    function open(msg) {
        leaveDiff();
        var old = currentModel();
        var model = monacoApi.editor.createModel(msg.content, msg.language);
        editor.setModel(model);
        editor.updateOptions({ readOnly: !!msg.readOnly });
        savedVersionId = versionOf(model);
        if (old) {
            old.dispose();
        }
        model.onDidChangeContent(reportChange);
        editor.focus();
    }

    function showDiff(msg) {
        if (!diffEditor) {
            diffEditor = monacoApi.editor.createDiffEditor(diffHost, {
                automaticLayout: true,
                readOnly: true,
                originalEditable: false,
                renderSideBySide: true,
                fontFamily: "'Cascadia Mono', Consolas, monospace",
                fontSize: 13,
                minimap: { enabled: false }
            });
            bindSave(diffEditor.getModifiedEditor());
        }
        diffModels.forEach(function (m) { m.dispose(); });
        diffModels = [
            monacoApi.editor.createModel(msg.original, msg.language),
            monacoApi.editor.createModel(msg.modified, msg.language)
        ];
        diffEditor.setModel({ original: diffModels[0], modified: diffModels[1] });
        inDiff = true;
        editorHost.style.display = 'none';
        diffHost.style.display = 'block';
        diffEditor.layout();
    }

    function onHostMessage(msg) {
        if (!msg || typeof msg.type !== 'string') {
            return;
        }
        switch (msg.type) {
            case 'open':
                open(msg);
                break;
            case 'showDiff':
                showDiff(msg);
                break;
            case 'hideDiff':
                leaveDiff();
                break;
            case 'requestContent': {
                var model = currentModel();
                post({
                    type: 'content',
                    requestId: msg.requestId,
                    text: model ? model.getValue() : '',
                    versionId: model ? versionOf(model) : 0
                });
                break;
            }
            case 'markSaved':
                savedVersionId = msg.versionId;
                reportChange();
                break;
            case 'focus':
                (inDiff ? diffEditor.getModifiedEditor() : editor).focus();
                break;
            default:
                console.warn('Unknown host message: ' + msg.type);
        }
    }

    function start(monaco) {
        monacoApi = monaco;
        defineTheme(monaco);
        editor = monaco.editor.create(editorHost, {
            model: monaco.editor.createModel('', 'plaintext'),
            theme: 'agentstudio-dark',
            automaticLayout: true,
            fontFamily: "'Cascadia Mono', Consolas, monospace",
            fontSize: 13,
            minimap: { enabled: true },
            scrollBeyondLastLine: false,
            renderWhitespace: 'selection',
            tabSize: 4
        });
        monaco.editor.setTheme('agentstudio-dark');
        bindSave(editor);
        editor.getModel().onDidChangeContent(reportChange);
        if (webview) {
            webview.addEventListener('message', function (event) { onHostMessage(event.data); });
        }
        post({ type: 'ready' });
    }

    if (typeof require === 'undefined') {
        fail('Monaco loader not found');
        return;
    }
    require.config({ paths: { vs: '../monaco/vs' } });
    require(['vs/editor/editor.main'], function () { start(window.monaco); }, fail);
}());
