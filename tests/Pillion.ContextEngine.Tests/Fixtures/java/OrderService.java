package com.example.orders;

import java.util.ArrayList;
import java.util.List;

/**
 * Manages orders.
 */
@Service
public class OrderService {
    private final List<String> orders = new ArrayList<>();
    private int created;

    public OrderService() {
        created = 0;
    }

    /** Places an order. */
    @Transactional
    public void place(String order) {
        orders.add(order);
        created++;
    }

    public int count() {
        return orders.size();
    }
}
