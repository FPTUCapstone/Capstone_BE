/* Linux document sandbox. Fail closed if any required restriction cannot be installed. */
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <grp.h>
#include <linux/landlock.h>
#include <sched.h>
#include <seccomp.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/prctl.h>
#include <sys/resource.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <unistd.h>

/* Bookworm headers predate ABI 3; the runtime ABI check below is mandatory. */
#ifndef LANDLOCK_ACCESS_FS_TRUNCATE
#define LANDLOCK_ACCESS_FS_TRUNCATE (1ULL << 14)
#endif

static _Noreturn void fail(const char *stage) { perror(stage); exit(125); }
static int landlock_supported(long abi, int probe_error) {
    if (abi < 0) {
        fprintf(stderr, "Landlock ABI probe failed: result=%ld errno=%d (%s); required>=3\n",
            abi, probe_error, strerror(probe_error));
        return 0;
    }
    if (abi < 3) {
        fprintf(stderr, "Landlock ABI unsupported: detected=%ld required>=3\n", abi);
        return 0;
    }
    return 1;
}
static void limit(int resource, rlim_t value) {
    struct rlimit bound = {value, value};
    if (setrlimit(resource, &bound)) fail("resource limit");
}
static void allow_path(int ruleset, const char *path, uint64_t rights, int optional) {
    int fd = open(path, O_PATH | O_CLOEXEC);
    if (fd < 0) { if (optional && errno == ENOENT) return; fail("sandbox path"); }
    struct landlock_path_beneath_attr rule = {.allowed_access = rights, .parent_fd = fd};
    if (syscall(SYS_landlock_add_rule, ruleset, LANDLOCK_RULE_PATH_BENEATH, &rule, 0)) fail("filesystem rule");
    close(fd);
}

int main(int argc, char **argv) {
    if (argc < 3) return 125;
    char *work = realpath(argv[1], NULL);
    if (!work || strcmp(work, argv[1]) || strncmp(work, "/tmp/tripmate-render-", 21)
        || strchr(work + 21, '/')) return 125;
    struct stat state;
    if (lstat(work, &state) || !S_ISDIR(state.st_mode)) return 125;
    int self_test = !strcmp(argv[2], "--self-test");
    if (!self_test && strcmp(argv[2], "/usr/lib/libreoffice/program/soffice.bin") && strcmp(argv[2], "/usr/bin/pdftotext")) return 125;
    if (geteuid() == 0) {
        if (chown(work, 65534, 65534) || setgroups(0, NULL) || setgid(65534) || setuid(65534)) fail("drop privileges");
    }
    if (geteuid() == 0) return 125;
    if (prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0)) fail("no new privileges");
    if (chdir(work) || chmod(work, 0700)) fail("private work directory");
    /* Close inherited descriptors before installing the allowlist, including service sockets. */
    if (syscall(SYS_close_range, 3U, ~0U, 0)) fail("close inherited descriptors");
    if (clearenv() || setenv("HOME", work, 1) || setenv("TMPDIR", work, 1)
        || setenv("PATH", "/usr/bin:/bin", 1) || setenv("LANG", "C.UTF-8", 1)
        || setenv("SAL_USE_VCLPLUGIN", "svp", 1) || setenv("OSL_SOCKET_PATH", work, 1)) fail("clean environment");
    limit(RLIMIT_CORE, 0);
    limit(RLIMIT_AS, 768UL * 1024 * 1024);
    limit(RLIMIT_CPU, 45);
    limit(RLIMIT_FSIZE, 50UL * 1024 * 1024);
    limit(RLIMIT_NOFILE, 128);
    limit(RLIMIT_NPROC, 64);
    cpu_set_t available, selected;
    if (sched_getaffinity(0, sizeof(available), &available)) fail("CPU affinity");
    CPU_ZERO(&selected);
    for (int i = 0; i < CPU_SETSIZE; i++) if (CPU_ISSET(i, &available)) { CPU_SET(i, &selected); break; }
    if (sched_setaffinity(0, sizeof(selected), &selected)) fail("CPU limit");

    /* ABI 3 is required: it includes cross-directory REFER and file TRUNCATE mediation. */
    long abi = syscall(SYS_landlock_create_ruleset, NULL, 0, LANDLOCK_CREATE_RULESET_VERSION);
    int probe_error = abi < 0 ? errno : 0;
    if (!landlock_supported(abi, probe_error)) return 125;
    uint64_t read = LANDLOCK_ACCESS_FS_EXECUTE | LANDLOCK_ACCESS_FS_READ_FILE | LANDLOCK_ACCESS_FS_READ_DIR;
    uint64_t write = LANDLOCK_ACCESS_FS_WRITE_FILE | LANDLOCK_ACCESS_FS_REMOVE_DIR | LANDLOCK_ACCESS_FS_REMOVE_FILE
        | LANDLOCK_ACCESS_FS_MAKE_DIR | LANDLOCK_ACCESS_FS_MAKE_REG | LANDLOCK_ACCESS_FS_MAKE_SYM | LANDLOCK_ACCESS_FS_MAKE_SOCK
        | LANDLOCK_ACCESS_FS_REFER | LANDLOCK_ACCESS_FS_TRUNCATE;
    struct landlock_ruleset_attr policy = {.handled_access_fs = (1ULL << 15) - 1};
    int ruleset = syscall(SYS_landlock_create_ruleset, &policy, sizeof(policy), 0);
    if (ruleset < 0) fail("filesystem isolation unavailable");
    allow_path(ruleset, "/usr", read, 0);
    allow_path(ruleset, "/lib", read, 1);
    allow_path(ruleset, "/lib64", read, 1);
    allow_path(ruleset, "/etc/fonts", read, 1);
    allow_path(ruleset, "/etc/libreoffice", read, 1);
    allow_path(ruleset, "/etc/ld.so.cache", LANDLOCK_ACCESS_FS_READ_FILE, 1);
    allow_path(ruleset, "/etc/passwd", LANDLOCK_ACCESS_FS_READ_FILE, 0);
    allow_path(ruleset, "/etc/group", LANDLOCK_ACCESS_FS_READ_FILE, 0);
    allow_path(ruleset, "/etc/nsswitch.conf", LANDLOCK_ACCESS_FS_READ_FILE, 0);
    allow_path(ruleset, "/var/cache/fontconfig", read, 1);
    allow_path(ruleset, "/dev/null", LANDLOCK_ACCESS_FS_READ_FILE | LANDLOCK_ACCESS_FS_WRITE_FILE, 0);
    allow_path(ruleset, "/dev/urandom", LANDLOCK_ACCESS_FS_READ_FILE, 0);
    allow_path(ruleset, work, read | write, 0);
    if (syscall(SYS_landlock_restrict_self, ruleset, 0)) fail("filesystem isolation");
    close(ruleset);

    scmp_filter_ctx filter = seccomp_init(SCMP_ACT_ALLOW);
    if (!filter) fail("seccomp allocation");
    const char *denied[] = {"ptrace", "process_vm_readv", "process_vm_writev",
        "kill", "tkill", "tgkill", "pidfd_open", "pidfd_getfd", "pidfd_send_signal", "io_uring_setup", "io_uring_enter",
        "io_uring_register", "unshare", "setns", "setsid", "setpgid", "mount", "umount2", "bpf", "perf_event_open", "keyctl", "add_key", "request_key"};
    for (size_t i = 0; i < sizeof(denied) / sizeof(denied[0]); i++) {
        int syscall_number = seccomp_syscall_resolve_name(denied[i]);
        if (syscall_number == __NR_SCMP_ERROR || seccomp_rule_add(filter, SCMP_ACT_ERRNO(EPERM), syscall_number, 0)) fail("seccomp rule");
    }
    /* LibreOffice needs local Unix IPC. This dedicated service has no API/DB Unix sockets.
       No inherited sockets remain; every internet/packet/netlink socket family is denied. */
    if (seccomp_rule_add(filter, SCMP_ACT_ERRNO(EPERM), SCMP_SYS(socket), 1,
        SCMP_A0(SCMP_CMP_NE, AF_UNIX))) fail("socket isolation");
    if (seccomp_rule_add(filter, SCMP_ACT_ERRNO(EPERM), SCMP_SYS(socketpair), 1,
        SCMP_A0(SCMP_CMP_NE, AF_UNIX))) fail("socketpair isolation");
    if (seccomp_load(filter)) fail("network/process isolation");
    seccomp_release(filter);

    if (self_test) {
        int fd = open("/service/server.py", O_RDONLY);
        if (fd >= 0) return 125;
        fd = open("/proc/1/environ", O_RDONLY);
        if (fd >= 0) return 125;
        fd = socket(AF_INET, SOCK_STREAM, 0);
        if (fd >= 0 || errno != EPERM || getenv("RENDERER_API_KEY")) return 125;
        fd = socket(AF_INET6, SOCK_STREAM, 0);
        if (fd >= 0 || errno != EPERM) return 125;
        fd = open("self-test", O_CREAT | O_WRONLY, 0600);
        if (fd < 0) return 125;
        close(fd);
        return 0;
    }
    execv(argv[2], argv + 2);
    fail("document engine");
}
