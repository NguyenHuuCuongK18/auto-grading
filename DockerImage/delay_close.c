/*
 * delay_close.c - FIXED
 * Intercepts read, recv AND close to fix race conditions.
 */

#define _GNU_SOURCE
#include <dlfcn.h>
#include <unistd.h>
#include <sys/socket.h>
#include <sys/types.h>
#include <sys/stat.h>
#include <time.h> // for nanosleep if needed

/* Pointers to the real functions */
static ssize_t (*real_read)(int, void*, size_t) = NULL;
static ssize_t (*real_recv)(int, void*, size_t, int) = NULL;
static int (*real_close)(int) = NULL;

/*
 * Initialize the real function pointers on first use.
 */
static void init_real_funcs(void) {
    if (!real_read) real_read = dlsym(RTLD_NEXT, "read");
    if (!real_recv) real_recv = dlsym(RTLD_NEXT, "recv");
    if (!real_close) real_close = dlsym(RTLD_NEXT, "close");
}

/*
 * Check if a file descriptor is a socket
 */
static int is_socket(int fd) {
    struct stat st;
    if (fstat(fd, &st) == -1) {
        return 0;
    }
    return S_ISSOCK(st.st_mode);
}

/*
 * Intercepted close() function - THE MISSING PIECE
 */
int close(int fd) {
    init_real_funcs();

    // Check if we are closing a socket
    if (is_socket(fd)) {
        // SLEEP HERE: This forces the Server to wait 200ms before
        // actually telling the OS to close the connection.
        // This gives the Client time to receive data, process it,
        // and send its own FIN first.
        usleep(200000); // 200ms delay
    }

    return real_close(fd);
}

ssize_t read(int fd, void *buf, size_t count) {
    init_real_funcs();
    ssize_t result = real_read(fd, buf, count);
    
    // If read returned 0 (EOF) on a socket, delay to force ACK
    if (result == 0 && is_socket(fd)) {
        usleep(50000);  // 50ms delay
    }
    return result;
}

ssize_t recv(int fd, void *buf, size_t len, int flags) {
    init_real_funcs();
    ssize_t result = real_recv(fd, buf, len, flags);
    
    // If recv returned 0 (EOF) on a socket, delay to force ACK
    if (result == 0 && is_socket(fd)) {
        usleep(50000);  // 50ms delay
    }
    return result;
}