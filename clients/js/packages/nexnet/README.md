# @dtronix/nexnet

The NexNet client runtime for JavaScript: browsers (WebSocket) and Node 22+ (WebSocket, HttpSocket, TCP, TLS, UDS).
ES modules with JSDoc types and no runtime dependencies.

**Applications do not install this package.** It is not published to npm. `NexNet.Generator` emits a copy of these
sources (`src/**`) into the JavaScript output directory, next to the generated client, as `nexnet-runtime/`. See:

- [Multi-language Clients](../../../../docs/articles/multi-language-clients.md): opt-in, build options, parity, type mapping
- [JavaScript Client](../../../../docs/articles/javascript-client.md): getting started
- [JavaScript runtime contract](../../../../docs/internals/js-runtime-contract.md): the API generated code uses

## Working on the runtime

Run from the repository root (Node 22 or later):

```
npm ci --prefix clients/js
npm test --prefix clients/js
npm run typecheck --prefix clients/js
npm run lint --prefix clients/js
```

`npm run test:browser --prefix clients/js` runs the browser suites in Chromium (`npx playwright install chromium`
once). The workspace and the tools are described in
[Contributing (Java & JS)](../../../../docs/articles/contributing-multilang.md).

| Path | Contents |
|---|---|
| `src/index.js`, `src/browser.js`, `src/node.js` | Entry points: platform-neutral, browser, Node (adds the Node transports) |
| `src/contract/` | The frozen API generated code compiles against |
| `src/serialization/`, `src/values/` | MessagePack codec, formatters, value types |
| `src/session/`, `src/transport/` | Protocol session, framing, transports |
| `src/invocation/`, `src/pipes/`, `src/collections/` | Invocations, pipes and channels, synchronized lists |
| `src/reconnect/`, `src/pool/`, `src/logging/`, `src/metrics/`, `src/diagnostics/` | Reconnection, client pool, logging, metrics, leak counters and snapshots |
| `test/` | Vitest suites, including the golden vectors of `tests/golden` |

Every file under `src/` is embedded in `NexNet.Generator`, so a change here changes what consumers receive on their
next build. Keep the browser entry free of `node:` imports (`test/entry-purity.test.js`).

MIT License.
