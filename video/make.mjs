// Makes the MemControl product video with what Windows already has: Node and Chrome. Nothing is downloaded.
// It serves video.html to a headless Chrome, which draws and encodes the video and sends the files back.
//   node video/make.mjs [output folder] [frames=3,20,30]
// With "frames=..." only those moments are saved as pictures, for a quick look.
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const out = path.resolve(process.argv[2] || path.join(here, 'out'));
const query = process.argv[3] ? '?' + process.argv[3] : '';
const chrome = process.env.CHROME || 'C:/Program Files/Google/Chrome/Application/chrome.exe';
fs.mkdirSync(out, { recursive: true });
let browser;

const finish = code => {
  try { browser.kill(); } catch { }
  server.close();
  setTimeout(() => process.exit(code), 300);
};

const server = http.createServer((request, response) => {
  const url = new URL(request.url, 'http://localhost');
  if (request.method === 'GET') {
    response.setHeader('content-type', 'text/html; charset=utf-8');
    response.end(url.pathname === '/' ? fs.readFileSync(path.join(here, 'video.html')) : '');
    return;
  }
  const parts = [];
  request.on('data', part => parts.push(part));
  request.on('end', () => {
    const body = Buffer.concat(parts);
    if (url.pathname === '/log') console.log(body.toString());
    if (url.pathname === '/upload') {
      const name = path.basename(url.searchParams.get('name') || 'file');
      fs.writeFileSync(path.join(out, name), body);
      console.log('saved ' + name + ', ' + body.length + ' bytes');
    }
    response.end('ok');
    if (url.pathname === '/done') finish(0);
  });
});

server.listen(0, '127.0.0.1', () => {
  const address = 'http://127.0.0.1:' + server.address().port + '/' + query;
  browser = spawn(chrome, ['--headless=new', '--user-data-dir=' + path.join(out, 'chrome-profile'), '--no-first-run', '--disable-extensions', '--window-size=1920,1080', address], { stdio: 'ignore' });
  setTimeout(() => { console.log('gave up after 20 minutes'); finish(2); }, 20 * 60 * 1000);
});
