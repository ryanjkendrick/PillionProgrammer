#include <stdlib.h>
#include "list.h"

/* A node in the list. */
struct node {
    int value;
    struct node *next;
};

typedef struct {
    struct node *head;
    size_t length;
} list_t;

static int count = 0;

/* Pushes a value. */
int list_push(list_t *list, int value)
{
    struct node *n = malloc(sizeof *n);
    if (!n) {
        return -1;
    }
    n->value = value;
    n->next = list->head;
    list->head = n;
    list->length++;
    return 0;
}

void list_free(list_t *list)
{
    while (list->head) {
        struct node *next = list->head->next;
        free(list->head);
        list->head = next;
    }
}
