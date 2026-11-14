**Where the task lives.** Find the code the task is about: the source files and symbols a change would touch, the
tests that cover them now, and what the task leaves unclear. Localize from the inside out, as Agentless does:
candidate files first, then the classes and functions inside them, confirmed by reading the code. The lexical
candidates in the first message are a starting point, often right and often not; check them, and search for the
behaviour the task describes. Explain for each location why it is involved. In `locations`, list source files
(not tests), most likely first; in the text, also name the existing tests that exercise them. In `open_questions`,
list what a developer would have to ask the client, each with the answer you would assume.
