// SNP0019 fixture: nothing in App consumes System.Text — CS8019 fires on the
// global using exactly as it does for ordinary ones.
global using System.Text;
