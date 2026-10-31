# Queues

Actions run on **queues**. The default queue is **blocking**: one action at a time, in the order they were triggered. That keeps alerts from talking over each other.

- Make your own queues on [Config](#/config). A **concurrent** queue runs its actions side by side.
- Set an action's queue in the builder header.
- **Pause** a queue to hold its actions (they wait; *Always run* actions still run). **Clear** drops what is waiting.
- [Monitor](#/monitor) shows each queue's pending and running counts. The [Dashboard](#/dashboard) has *Pause queues* and *Resume queues* buttons for breaks.

Some events skip the queue (for example rewards you configure to skip it). The steps *Set Action Queue Pause State* and *Clear Action Queue* control queues from actions.

Pending actions survive a restart.
