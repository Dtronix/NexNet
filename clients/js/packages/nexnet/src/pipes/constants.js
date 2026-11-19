/**
 * Wire constants of pipes (protocol spec §4.6, §7).
 * @module
 */

/**
 * `DuplexPipeUpdateState` flags (§4.6). The client's writer is the client-to-server direction (bit 0), its reader
 * the server-to-client direction (bit 1).
 */
export const PipeState = Object.freeze({
  Unset: 0,
  ClientWriterServerReaderComplete: 1,
  ClientReaderServerWriterComplete: 2,
  Ready: 4,
  Refused: 8,
  Complete: 7,
});

/** The flag this (client) side sets when its writer completes. */
export const WRITER_COMPLETE = PipeState.ClientWriterServerReaderComplete;

/** The flag this (client) side sets when its reader completes. */
export const READER_COMPLETE = PipeState.ClientReaderServerWriterComplete;

/** Most send credit a peer may grant on one pipe (§7.2); more disconnects with `FlowControl`. */
export const MAX_CREDIT = 2 ** 31;

/** Largest `DuplexPipeWrite` payload: one frame body (§2). The pipe ID travels in the post-header. */
export const MAX_PIPE_CHUNK = 0xffff;
