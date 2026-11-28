/* Exercise the production diagnostic function without requiring a particular host ABI. */
#define main sandbox_main
#include "sandbox.c"
#undef main

int main(int argc, char **argv) {
    if (argc != 3) return 2;
    return landlock_supported(strtol(argv[1], NULL, 10), atoi(argv[2])) ? 0 : 125;
}
