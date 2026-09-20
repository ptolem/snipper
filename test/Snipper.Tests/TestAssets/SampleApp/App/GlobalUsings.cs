// SNP0019 fixture: nothing in App consumes System.Text — CS8019 fires on the
// global using exactly as it does for ordinary ones.
global using System.Text;
// Verbatim duplicate (the milkrun FP-4 shape): the compiler flags only this
// second occurrence — the message must say it is a duplicate so consumers know
// exactly one copy must remain.
global using System.Text;
