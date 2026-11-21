/**
 * Wire constants of pipes (protocol spec §4.6, §7).
 * @module
 */
/**
 * `DuplexPipeUpdateState` flags (§4.6). The client's writer is the client-to-server direction (bit 0), its reader
 * the server-to-client direction (bit 1).
 */
export declare const PipeState: Readonly<{
    Unset: 0;
    ClientWriterServerReaderComplete: 1;
    ClientReaderServerWriterComplete: 2;
    Ready: 4;
    Refused: 8;
    Complete: 7;
}>;
/** The flag this (client) side sets when its writer completes. */
export declare const WRITER_COMPLETE: 1;
/** The flag this (client) side sets when its reader completes. */
export declare const READER_COMPLETE: 2;
/** Most send credit a peer may grant on one pipe (§7.2); more disconnects with `FlowControl`. */
export declare const MAX_CREDIT: number;
/** Largest `DuplexPipeWrite` payload: one frame body (§2). The pipe ID travels in the post-header. */
export declare const MAX_PIPE_CHUNK = 65535;
