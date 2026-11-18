package com.dtronix.nexnet.session;

import java.util.List;

/**
 * Something with control frames to send (a pipe's window update or close acknowledgement). Queued with
 * {@link ControlSender#requestControl(ControlSource)}; the writer thread asks it for its frames when it drains.
 */
@FunctionalInterface
public interface ControlSource {
    /**
     * Appends the frames to send now. Called on the writer thread; must not block.
     *
     * @param frames the list to append to
     */
    void appendControlFrames(List<Frame> frames);
}
