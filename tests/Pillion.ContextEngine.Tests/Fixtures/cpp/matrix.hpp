#ifndef MATRIX_HPP
#define MATRIX_HPP

#include <vector>

namespace linalg {

/// A dense matrix.
template <typename T>
class Matrix {
public:
    Matrix(int rows, int cols) : rows_(rows), cols_(cols), data_(rows * cols) {}

    T& at(int r, int c) {
        return data_[r * cols_ + c];
    }

    int rows() const;

private:
    int rows_;
    int cols_;
    std::vector<T> data_;
};

template <typename T>
int Matrix<T>::rows() const {
    return rows_;
}

}  // namespace linalg

#endif
