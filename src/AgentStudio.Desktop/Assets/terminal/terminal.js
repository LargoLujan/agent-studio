// Bridge between xterm.js and the WPF host (TerminalControl). Messages are JSON strings.
//   page -> host: {type:'ready', cols, rows} | {type:'data', data} | {type:'resize', cols, rows}
//   host -> page: {type:'write', data} | {type:'reset'} | {type:'focus'}
(function () {
  'use strict';

  var host = window.chrome && window.chrome.webview;

  function post(message) {
    if (host) {
      host.postMessage(JSON.stringify(message));
    }
  }

  function showError(text) {
    var el = document.getElementById('error');
    el.textContent = text;
    el.hidden = false;
    post({ type: 'error', message: text });
  }

  if (typeof Terminal === 'undefined' || typeof FitAddon === 'undefined') {
    showError('Faltan los recursos de xterm.js. Ejecuta tools\\assets\\fetch-xterm.ps1 y vuelve a compilar.');
    return;
  }

  // Colours mirror Themes/Dark.xaml (the token each one comes from is named after it). WCAG 2.x AA is enforced by
  // tests/AgentStudio.Tests/Desktop/Contrast/WebThemeContrastTests.cs, which PARSES this literal: keep one
  // key: '#RRGGBB' per line.
  //
  // The ANSI palette is the xterm.js 6.0.0 default (Tango) with only the colours that fell under 4.5:1 on the terminal
  // background lightened towards white: red #cc0000, blue #3465a4, magenta #75507b and brightBlack #555753 (PowerShell
  // writes parameters and inline predictions in brightBlack). `black` stays dark on purpose: programs use it as the
  // background of coloured status lines, so it cannot also be legible as text; minimumContrastRatio (below) lifts
  // any text that is drawn with it, or with any other colour pair a program picks, to 4.5:1 at render time.
  var TERMINAL_THEME = {
    background: '#0A1020',                       // Background
    foreground: '#E8EEF9',                       // TextPrimary
    cursor: '#2E68F9',                           // Accent
    cursorAccent: '#FFFFFF',                     // OnAccent: the character under a block cursor
    selectionBackground: '#243558',              // Border
    selectionForeground: '#E8EEF9',              // TextPrimary: selected text keeps 4.5:1 whatever colour it had
    scrollbarSliderBackground: '#5F7396',        // BorderStrong: a thumb must be 3:1 on its track
    scrollbarSliderHoverBackground: '#7A8DB0',
    scrollbarSliderActiveBackground: '#2E68F9',
    black: '#2E3436',
    red: '#DB4C4C',
    green: '#4E9A06',
    yellow: '#C4A000',
    blue: '#5981B4',
    magenta: '#927597',
    cyan: '#06989A',
    white: '#D3D7CF',
    brightBlack: '#7C7E7B',
    brightRed: '#EF2929',
    brightGreen: '#8AE234',
    brightYellow: '#FCE94F',
    brightBlue: '#729FCF',
    brightMagenta: '#AD7FA8',
    brightCyan: '#34E2E2',
    brightWhite: '#EEEEEC'
  };

  var term = new Terminal({
    allowProposedApi: false,
    cursorBlink: true,
    scrollback: 10000,
    fontFamily: '"Cascadia Mono", Consolas, monospace',
    fontSize: 14,
    minimumContrastRatio: 4.5,
    theme: TERMINAL_THEME
  });
  var fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  term.open(document.getElementById('terminal'));

  var lastCols = 0;
  var lastRows = 0;

  function refit() {
    try {
      fit.fit();
    } catch (e) {
      return;
    }
    if (term.cols !== lastCols || term.rows !== lastRows) {
      lastCols = term.cols;
      lastRows = term.rows;
      post({ type: 'resize', cols: term.cols, rows: term.rows });
    }
  }

  term.onData(function (data) {
    post({ type: 'data', data: data });
  });

  var resizeTimer = 0;
  new ResizeObserver(function () {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(refit, 30);
  }).observe(document.getElementById('terminal'));

  if (host) {
    host.addEventListener('message', function (event) {
      var message;
      try {
        message = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
      } catch (e) {
        return;
      }
      if (message.type === 'write') {
        term.write(message.data);
      } else if (message.type === 'reset') {
        term.reset();
      } else if (message.type === 'focus') {
        term.focus();
      }
    });
  }

  fit.fit();
  lastCols = term.cols;
  lastRows = term.rows;
  post({ type: 'ready', cols: term.cols, rows: term.rows });
  term.focus();
})();
