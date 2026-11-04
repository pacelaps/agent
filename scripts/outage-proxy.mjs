#!/usr/bin/env node
// A local proxy that can cut the agent off from the network on command (step 5.6: resync, handover, local failsafe),
// and only the agent: it is started with HTTPS_PROXY pointing here, so iRacing, the browser and everything else keep
// their network. No administrator rights, nothing changed on the server.
//
//   node scripts/outage-proxy.mjs [--port=8899]
//
// Start the agent from another PowerShell window (exit a running one first):
//
//   $env:HTTPS_PROXY = 'http://127.0.0.1:8899'; & '<folder>\pacelaps-agent.exe'
//
// Then type here (and Enter):
//
//   block 40    cut it off for 40 s          block    until you type unblock
//   unblock     end a block                  status   tunnels and the block
//   quit        stop the proxy (the agent then has no network until it is restarted without HTTPS_PROXY)
//
// A block behaves like a dead network, not like a refusal: open connections stop carrying data and new ones are
// never answered, so the agent notices through its own timeouts. When the block ends, the connections that lived
// through it are closed (their data is gone) and new ones go through again. Every block is written with its UTC
// start and end to %LOCALAPPDATA%\PaceLaps\Agent\logs\block-agent.log. Nothing of the traffic is logged.

import { appendFileSync, mkdirSync } from 'node:fs';
import net from 'node:net';
import { dirname, join } from 'node:path';
import readline from 'node:readline';

const args = Object.fromEntries(process.argv.slice(2).map((a) => {
    const [k, v] = a.replace(/^--/, '').split('=');
    return [k, v ?? true];
}));
const port = Number(args.port ?? 8899);
const logFile = join(process.env.LOCALAPPDATA ?? '.', 'PaceLaps', 'Agent', 'logs', 'block-agent.log');

/** @type {Set<{client: net.Socket, upstream: net.Socket|null, target: string, cut: boolean}>} */
const tunnels = new Set();
let blockedSince = null;
let timer = null;

const time = (d = new Date()) => d.toISOString().slice(11, 19);

function close(t) {
    t.client.destroy();
    t.upstream?.destroy();
    tunnels.delete(t);
}

const server = net.createServer((client) => {
    client.once('data', (head) => {
        const line = head.toString('latin1').split('\r\n', 1)[0];
        const match = /^CONNECT ([^\s:]+):(\d+) HTTP\/1\.[01]$/.exec(line);
        if (!match) {
            // The agent talks HTTPS and WSS only, both through CONNECT.
            client.end('HTTP/1.1 501 Not Implemented\r\nConnection: close\r\n\r\n');
            return;
        }

        const t = { client, upstream: null, target: `${match[1]}:${match[2]}`, cut: false };
        tunnels.add(t);
        client.on('error', () => close(t));
        client.on('close', () => close(t));
        if (blockedSince) {
            // A dead network: the request is never answered.
            t.cut = true;
            client.on('data', () => {});
            return;
        }

        console.log(`${time()}  tunnel to ${t.target}`);
        t.upstream = net.connect(Number(match[2]), match[1], () => {
            client.write('HTTP/1.1 200 Connection Established\r\n\r\n');
        });
        t.upstream.on('error', () => close(t));
        t.upstream.on('close', () => close(t));
        client.on('data', (d) => { if (!t.cut) t.upstream.write(d); });
        t.upstream.on('data', (d) => { if (!t.cut) client.write(d); });
    });
});

function block(seconds) {
    if (blockedSince) {
        console.log('Already blocked.');
        return;
    }
    blockedSince = new Date();
    for (const t of tunnels) {
        t.cut = true;
    }
    console.log(`${time(blockedSince)}  BLOCKED (UTC)${seconds > 0 ? ` for ${seconds} s` : ': type unblock to end it'}`);
    if (seconds > 0) {
        timer = setTimeout(unblock, seconds * 1000);
    }
}

function unblock() {
    if (!blockedSince) {
        console.log('Not blocked.');
        return;
    }
    clearTimeout(timer);
    const from = blockedSince;
    const to = new Date();
    blockedSince = null;
    let closed = 0;
    for (const t of [...tunnels]) {
        if (t.cut) {
            close(t);
            closed++;
        }
    }
    const seconds = Math.round((to - from) / 1000);
    console.log(`${time(to)}  unblocked (UTC) after ${seconds} s; ${closed} dead connection(s) closed`);
    try {
        mkdirSync(dirname(logFile), { recursive: true });
        appendFileSync(logFile, `${from.toISOString()} ${to.toISOString()} ${seconds} s outage-proxy\n`);
    } catch (e) {
        console.log(`Could not write ${logFile}: ${e.message}`);
    }
}

server.on('error', (e) => {
    console.error(`The proxy cannot listen on 127.0.0.1:${port}: ${e.message}`);
    process.exit(1);
});
server.listen(port, '127.0.0.1', () => {
    console.log(`Outage proxy on 127.0.0.1:${port}. Start the agent with:`);
    console.log(`  $env:HTTPS_PROXY = 'http://127.0.0.1:${port}'; & '<folder>\\pacelaps-agent.exe'`);
    console.log('Commands: block [SECONDS] | unblock | status | quit');
});

readline.createInterface({ input: process.stdin }).on('line', (input) => {
    const [command, value] = input.trim().split(/\s+/);
    switch (command) {
        case 'block':
            block(Number(value ?? 0) || 0);
            break;
        case 'unblock':
            unblock();
            break;
        case 'status':
            console.log(`${blockedSince ? `BLOCKED since ${time(blockedSince)} UTC` : 'not blocked'}; ${tunnels.size} connection(s): ${[...tunnels].map((t) => t.target + (t.cut ? ' (cut)' : '')).join(', ') || 'none'}`);
            break;
        case 'quit':
        case 'exit':
            if (blockedSince) unblock();
            process.exit(0);
            break;
        case undefined:
        case '':
            break;
        default:
            console.log('Commands: block [SECONDS] | unblock | status | quit');
    }
});
